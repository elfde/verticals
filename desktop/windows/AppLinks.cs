// What the app builds from a task's link and the lines that connect it to Claude Code and to Codex: the Windows twin of
// desktop/macos/AppLinks.swift, with the same names and rules (docs/design/task-sessions.md). No window in it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Verticals
{
    static class AppLinks
    {
        // ---------------------------------------------------------------- a task's link

        /// A task's id is what the board's address and the agents' link tool both carry: letters, digits, `-` and `_`, up to 100.
        public static bool IsTaskID(string text) =>
            text != null && text.Length >= 1 && text.Length <= 100
            && text.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_'));

        /// `verticals://goal/<id>` is a task; anything else is not a link of the app's. A trailing slash is allowed.
        public static string TaskID(string link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
            if (!uri.Scheme.Equals("verticals", StringComparison.OrdinalIgnoreCase)) return null;
            if (!uri.Host.Equals("goal", StringComparison.OrdinalIgnoreCase)) return null;
            var id = uri.AbsolutePath.TrimStart('/').TrimEnd('/');
            return IsTaskID(id) ? id : null;
        }

        /// What the board reads after `#` to open the task (web/src/lib/urlState.ts).
        public static string Fragment(string id) => "goal/" + id;

        /// The board's address for the task, to load when the page is not there yet or is somewhere else.
        public static Uri BoardURL(string id, Uri ui) =>
            IsTaskID(id) ? new UriBuilder(ui) { Path = "/", Fragment = Fragment(id) }.Uri : null;

        // ---------------------------------------------------------------- the lines that connect an agent

        public const string ClaudeServer = "verticals-desktop";
        public const string CodexServer = "verticals";

        /// `claude mcp add-json`: the address, and a command that prints the token from the owner-only file when Claude Code
        /// connects, so the token is never written into Claude Code's own settings. Claude Code runs it with cmd, so `type`,
        /// where macOS has `cat` (a Windows path cannot hold a quote).
        public static string[] ClaudeAddArguments(string mcpURL, string headersFile)
        {
            var json = "{" + string.Join(",", new[] {
                Json("headersHelper") + ":" + Json("type \"" + headersFile + "\""),
                Json("type") + ":" + Json("http"),
                Json("url") + ":" + Json(mcpURL),
            }) + "}";
            return new[] { "mcp", "add-json", "--scope", "user", ClaudeServer, json };
        }

        public static readonly string[] ClaudeRemoveArguments = { "mcp", "remove", "--scope", "user", ClaudeServer };

        /// `codex mcp add`: Codex starts the relay of desktop/chat/mcp_proxy.py, told the address and the file with the token.
        public static string[] CodexAddArguments(string python, string relay, string mcpURL, string headersFile) => new[] {
            "mcp", "add", CodexServer, "--env", "VERTICALS_MCP_URL=" + mcpURL, "--env", "VERTICALS_MCP_HEADERS=" + headersFile,
            "--", python, relay };

        public static readonly string[] CodexRemoveArguments = { "mcp", "remove", CodexServer };

        // ---------------------------------------------------------------- whose sign-in the agents use

        public static readonly HashSet<string> AgentVariables = new HashSet<string> { "CLAUDE_CONFIG_DIR", "CODEX_HOME" };

        /// `agents.env` in the state folder: `KEY=VALUE` lines, `#` comments skipped, quotes dropped, a leading `~/` or `~\` made
        /// the home folder. Only AgentVariables are kept, and only with a full path (`C:\…`): a typo is ignored.
        public static Dictionary<string, string> AgentEnvironment(string text, string home)
        {
            var found = new Dictionary<string, string>();
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq < 0) continue;
                var key = line.Substring(0, eq).Trim();
                if (key.StartsWith("export ")) key = key.Substring(7).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == value[value.Length - 1] && (value[0] == '"' || value[0] == '\''))
                    value = value.Substring(1, value.Length - 2);
                if (value == "~") value = home;
                else if (value.StartsWith("~/") || value.StartsWith("~\\")) value = Path.Combine(home, value.Substring(2));
                if (!AgentVariables.Contains(key) || !IsFullPath(value)) continue;
                found[key] = value;
            }
            return found;
        }

        static bool IsFullPath(string path) =>
            path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

        // ---------------------------------------------------------------- small helpers

        public static string Json(string s)
        {
            var b = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c);
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        /// One argument as CommandLineToArgvW reads it back: quotes when needed, backslashes before a quote doubled.
        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var b = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                b.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
