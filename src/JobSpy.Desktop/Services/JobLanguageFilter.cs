using System.Text.RegularExpressions;

namespace JobSpy.Desktop.Services;

internal static class JobLanguageFilter
{
    private static readonly string[] OtherLanguagePatterns =
    {
        @"\bJavaScript\b", @"\bECMAScript\b", @"\bTypeScript\b", @"\bNode\.?js\b", @"\bGolang\b",
        @"(?-i:\bGo)(?=,|\.)", @"\bC\+\+", @"\bObjective[- ]C\b", @"\bVisual Basic\b", @"\bVBScript\b",
        @"\bPython\b", @"\bJava\b", @"\bRuby\b", @"\bPHP\b", @"\bRust\b", @"\bKotlin\b", @"\bSwift\b",
        @"\bScala\b", @"\bGroovy\b", @"\bPerl\b", @"\bDart\b", @"\bElixir\b", @"\bErlang\b", @"\bHaskell\b",
        @"\bClojure\b", @"\bF\s*#", @"\bPowerShell\b", @"\bBash\b", @"\bShell scripting\b", @"\bPL/SQL\b",
        @"\bTransact-SQL\b", @"\bT-SQL\b", @"\bSQL\b", @"\bMATLAB\b", @"\bJulia\b", @"\bLua\b", @"\bFortran\b",
        @"\bCOBOL\b", @"\bAssembly\b", @"\bSolidity\b", @"\bApex\b", @"\bABAP\b", @"\bDelphi\b", @"\bVBA\b",
        @"\bSAS\b", @"\bLisp\b", @"\bOCaml\b", @"\bPascal\b", @"\bProlog\b", @"\bSmalltalk\b", @"\bScheme\b",
        @"\bAda\b", @"\bTcl\b", @"\bAWK\b", @"\bElm\b", @"\bCrystal\b", @"\bZig\b", @"\bNim\b",
        @"\bR\s+(?:programming|language|script)\b", @"\bQ\s*#",
    };

    private static readonly Regex LanguageMentionPattern = new(
        $@"(?<target>\.NET\b|\bdotnet\b|\bC\s*#|\bC\s+sharp\b)|(?<other>{string.Join("|", OtherLanguagePatterns)})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsRelevant(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return true;
        }

        var firstLanguageMention = LanguageMentionPattern.Match(description);
        if (!firstLanguageMention.Success || firstLanguageMention.Groups["target"].Success)
        {
            return true;
        }

        var lineStart = description.LastIndexOf('\n', firstLanguageMention.Index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = description.IndexOf('\n', firstLanguageMention.Index);
        if (lineEnd < 0)
        {
            lineEnd = description.Length;
        }

        foreach (Match languageMention in LanguageMentionPattern.Matches(description[lineStart..lineEnd]))
        {
            if (languageMention.Groups["target"].Success)
            {
                return true;
            }
        }

        return false;
    }
}