using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JobSpy.Desktop.Services;

internal static class CodexGoalRunner
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    public static bool Run(string workspace, string objective, string initialPrompt)
    {
        try
        {
            return RunAsync(workspace, objective, initialPrompt).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Codex goal failed: {exception.GetBaseException().Message}");
            return false;
        }
    }

    private static async Task<bool> RunAsync(string workspace, string objective, string initialPrompt)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "codex",
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "app-server", "--listen", "stdio://", "--enable", "goals", "--config", "mcp_servers={}",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        var standardError = process.StandardError.ReadToEndAsync();
        string? threadId = null;
        try
        {
            using var timeout = new CancellationTokenSource(RunTimeout);
            var client = new AppServerClient(process);
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "jobspy", title = "JobSpy", version = "1.0.0" },
                capabilities = new { experimentalApi = true },
            }, timeout.Token).ConfigureAwait(false);
            await client.NotifyAsync("initialized", new { }, timeout.Token).ConfigureAwait(false);

            using var threadResponse = await client.RequestAsync("thread/start", new
            {
                cwd = workspace,
                model = "gpt-6-luna",
                approvalPolicy = "never",
                sandbox = "workspace-write",
                ephemeral = false,
                config = new { mcp_servers = new { } },
            }, timeout.Token).ConfigureAwait(false);
            threadId = threadResponse.RootElement.GetProperty("thread").GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(threadId))
            {
                throw new InvalidOperationException("Codex did not return a thread ID.");
            }

            await client.RequestAsync("thread/goal/set", new
            {
                threadId,
                objective,
                status = "active",
                origin = "user",
            }, timeout.Token).ConfigureAwait(false);

            await client.RequestAsync("turn/start", new
            {
                threadId,
                cwd = workspace,
                model = "gpt-6-luna",
                effort = "medium",
                input = new[] { new { type = "text", text = initialPrompt } },
            }, timeout.Token).ConfigureAwait(false);

            var completed = await client.WaitForGoalCompletionAsync(threadId, timeout.Token).ConfigureAwait(false);
            if (!completed)
            {
                Debug.WriteLine("Codex goal did not complete.");
            }

            return completed;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(threadId) && !process.HasExited)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var client = new AppServerClient(process);
                    await client.RequestAsync("thread/delete", new { threadId }, cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Could not remove temporary Codex goal: {exception.GetBaseException().Message}");
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            if (!standardError.IsCompleted)
            {
                _ = standardError.ContinueWith(task =>
                    Debug.WriteLine($"Codex app-server error: {task.Result.Trim()}"),
                    TaskScheduler.Default);
            }
        }
    }

    private sealed class AppServerClient(Process process)
    {
        private int _requestId;

        public async Task<JsonDocument> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            var requestId = Interlocked.Increment(ref _requestId);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters,
            }).AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                using var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var responseId)
                    || responseId.ValueKind != JsonValueKind.Number
                    || responseId.GetInt32() != requestId)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var errorMessage = error.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString()
                        : error.ToString();
                    throw new InvalidOperationException($"Codex {method} failed: {errorMessage}");
                }

                return JsonDocument.Parse(root.GetProperty("result").GetRawText());
            }
        }

        public async Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            }).AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> WaitForGoalCompletionAsync(string threadId, CancellationToken cancellationToken)
        {
            while (true)
            {
                using var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                var root = message.RootElement;
                if (!root.TryGetProperty("method", out var methodValue)
                    || !root.TryGetProperty("params", out var parameters)
                    || !parameters.TryGetProperty("threadId", out var responseThreadId)
                    || !string.Equals(responseThreadId.GetString(), threadId, StringComparison.Ordinal))
                {
                    continue;
                }

                var method = methodValue.GetString();
                if (method == "thread/goal/updated"
                    && parameters.TryGetProperty("goal", out var updatedGoal)
                    && updatedGoal.TryGetProperty("status", out var updatedStatus)
                    && updatedStatus.GetString() is "blocked" or "usageLimited" or "budgetLimited")
                {
                    return false;
                }

                if (method != "turn/completed" || !parameters.TryGetProperty("turn", out var turn))
                {
                    continue;
                }

                if (!turn.TryGetProperty("status", out var turnStatus) || turnStatus.GetString() != "completed")
                {
                    return false;
                }

                using var goalResponse = await RequestAsync("thread/goal/get", new { threadId }, cancellationToken)
                    .ConfigureAwait(false);
                if (!goalResponse.RootElement.TryGetProperty("goal", out var goal)
                    || goal.ValueKind == JsonValueKind.Null)
                {
                    return false;
                }

                if (goal.GetProperty("status").GetString() == "complete")
                {
                    return true;
                }

                var status = goal.GetProperty("status").GetString();
                if (status is "blocked" or "usageLimited" or "budgetLimited" or "paused")
                {
                    return false;
                }
            }
        }

        private async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException("Codex app-server closed its output stream.");
            }

            return JsonDocument.Parse(line);
        }
    }
}
