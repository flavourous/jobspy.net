using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JobSpy.Desktop.Services;

internal static class CodexTurnRunner
{
    public static Session StartSession() => new();

    internal sealed class Session : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private readonly AppServerClient _client;
        private readonly CancellationTokenSource _sessionCancellation = new();
        private bool _disposed;

        public Session()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "codex",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "app-server", "--listen", "stdio://", "--config", "mcp_servers={}",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Codex app-server could not be started.");
            _standardError = _process.StandardError.ReadToEndAsync();
            _client = new AppServerClient(_process);
            InitializeAsync().GetAwaiter().GetResult();
        }

        public async Task<string?> RunAsync(string workspace, string prompt)
        {
            if (_disposed || _process.HasExited)
            {
                return null;
            }

            try
            {
                return await RunCoreAsync(workspace, prompt, _sessionCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Codex review turn failed: {exception.GetBaseException().Message}");
                return null;
            }
        }

        private async Task InitializeAsync()
        {
            var cancellationToken = _sessionCancellation.Token;
            await _client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "jobspy", title = "JobSpy", version = "1.0.0" },
                capabilities = new { experimentalApi = true },
            }, cancellationToken).ConfigureAwait(false);
            await _client.NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string?> RunCoreAsync(
            string workspace,
            string prompt,
            CancellationToken cancellationToken)
        {
            string? threadId = null;
            try
            {
                using var threadResponse = await _client.RequestAsync("thread/start", new
                {
                    cwd = workspace,
                    model = "gpt-6-luna",
                    approvalPolicy = "never",
                    sandbox = "read-only",
                    ephemeral = false,
                    config = new { mcp_servers = new { } },
                }, cancellationToken).ConfigureAwait(false);
                threadId = threadResponse.RootElement.GetProperty("thread").GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(threadId))
                {
                    throw new InvalidOperationException("Codex did not return a thread ID.");
                }

                var turnCompletion = _client.RegisterTurnCompletion(threadId);
                await _client.RequestAsync("turn/start", new
                {
                    threadId,
                    cwd = workspace,
                    model = "gpt-6-luna",
                    effort = "low",
                    input = new[] { new { type = "text", text = prompt } },
                }, cancellationToken).ConfigureAwait(false);

                var response = await turnCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
                _client.RemoveTurnCompletion(threadId);
                if (response is null)
                {
                    Debug.WriteLine("Codex turn did not complete.");
                }

                return response;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    _client.RemoveTurnCompletion(threadId);
                }

                if (!string.IsNullOrWhiteSpace(threadId) && !_process.HasExited)
                {
                    try
                    {
                        await _client.RequestAsync("thread/delete", new { threadId }, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Could not remove temporary Codex review thread: {exception.GetBaseException().Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sessionCancellation.Cancel();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }

            _client.ReaderCompletion.GetAwaiter().GetResult();

            if (_standardError.IsCompleted)
            {
                Debug.WriteLine($"Codex app-server stderr: {_standardError.GetAwaiter().GetResult().Trim()}");
            }
            else
            {
                _ = _standardError.ContinueWith(task =>
                    Debug.WriteLine($"Codex app-server stderr: {task.Result.Trim()}"),
                    TaskScheduler.Default);
            }

            _sessionCancellation.Dispose();
            _process.Dispose();
        }
    }

    private sealed class AppServerClient
    {
        private readonly Process _process;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _responses = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _turns = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Task _readerTask;
        private int _requestId;

        public AppServerClient(Process process)
        {
            _process = process;
            _readerTask = Task.Run(ReadLoopAsync);
        }

        public Task ReaderCompletion => _readerTask;

        public async Task<JsonDocument> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            var requestId = Interlocked.Increment(ref _requestId);
            var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_responses.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("Could not register a Codex app-server request.");
            }

            try
            {
                await WriteMessageAsync(new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters,
                }, cancellationToken).ConfigureAwait(false);
                return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _responses.TryRemove(requestId, out _);
                throw;
            }
        }

        public Task<string?> RegisterTurnCompletion(string threadId)
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_turns.TryAdd(threadId, completion))
            {
                throw new InvalidOperationException($"A Codex turn is already registered for thread {threadId}.");
            }

            return completion.Task;
        }

        public void RemoveTurnCompletion(string threadId) => _turns.TryRemove(threadId, out _);

        public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
            WriteMessageAsync(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            }, cancellationToken);

        private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (true)
                {
                    var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        throw new InvalidOperationException("Codex app-server closed its output stream.");
                    }

                    using var message = JsonDocument.Parse(line);
                    Dispatch(message.RootElement);
                }
            }
            catch (Exception exception)
            {
                foreach (var (requestId, completion) in _responses)
                {
                    if (_responses.TryRemove(requestId, out _))
                    {
                        completion.TrySetException(exception);
                    }
                }

                foreach (var (threadId, completion) in _turns)
                {
                    if (_turns.TryRemove(threadId, out _))
                    {
                        completion.TrySetException(exception);
                    }
                }
            }
        }

        private void Dispatch(JsonElement root)
        {
            if (root.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.TryGetInt32(out var requestId)
                && _responses.TryRemove(requestId, out var responseCompletion))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var errorMessage = error.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString()
                        : error.ToString();
                    responseCompletion.TrySetException(new InvalidOperationException($"Codex app-server request failed: {errorMessage}"));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    responseCompletion.TrySetResult(JsonDocument.Parse(result.GetRawText()));
                }
                else
                {
                    responseCompletion.TrySetException(new InvalidOperationException("Codex app-server returned an empty response."));
                }

                return;
            }

            if (!root.TryGetProperty("method", out var method)
                || method.GetString() != "turn/completed"
                || !root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("threadId", out var threadIdValue)
                || threadIdValue.GetString() is not { } threadId
                || !_turns.TryRemove(threadId, out var turnCompletion)
                || !parameters.TryGetProperty("turn", out var turn))
            {
                return;
            }

            if (!turn.TryGetProperty("status", out var status) || status.GetString() != "completed")
            {
                turnCompletion.TrySetResult(null);
                return;
            }

            string? agentMessage = null;
            if (turn.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type)
                        && type.GetString() == "agentMessage"
                        && item.TryGetProperty("text", out var text))
                    {
                        agentMessage = text.GetString();
                    }
                }
            }

            turnCompletion.TrySetResult(agentMessage);
        }
    }
}
