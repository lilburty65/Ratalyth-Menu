/*
 * Ratalyth Menu  Managers/URLBlocker.cs
 * A community driven mod menu for Gorilla Tag with over 1000+ mods
 *
 * Copyright (C) 2026  Seralyth Software
 * Copyright (C) 2026  Ratalyth
 * https://github.com/Ratalyth/Ratalyth-Menu
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

// The purpose of this class is to block known malicious URLs from being accessed by the game or mods
using HarmonyLib;
using Ratalyth.Managers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;
using Valve.Newtonsoft.Json;

namespace Ratalyth.Patches.Safety
{
    public class URLBlocker
    {
        private static Dictionary<string, string> banned = new Dictionary<string, string>();
        private static readonly object locker = new object();

        private static readonly HashSet<string> notifiedAssemblies = new HashSet<string>();
        private static readonly object notifyLocker = new object();

        private static readonly Stopwatch uptime = Stopwatch.StartNew();

        // One client for the lifetime of the process. This used to construct a new HttpClient on
        // every 30 second poll, which is the documented way to exhaust sockets: each instance
        // owns a connection pool that only gets reaped when it is finalised, so a loop that runs
        // for hours accumulates pools faster than the GC reclaims them.
        private static readonly HttpClient httpClient = new HttpClient();

        private static readonly ConcurrentQueue<(Level, string)> pendingLogs = new ConcurrentQueue<(Level, string)>();

        private static double lastSuccessfulLoad = -1d;
        private static bool stalenessWarned;

        private const double StaleAfterSeconds = 300d;

        /// <summary>
        /// LoadBanList is an async void loop, so everything after each await resumes on a thread-pool
        /// thread. Neither Unity's clock nor its logger can be touched from there, so messages are
        /// parked here and drained from Main.Update on the main thread.
        /// </summary>
        private static void QueueLog(Level level, string message) => pendingLogs.Enqueue((level, message));

        /// <summary>Called once per frame from Main.Update. Drains logs queued by the ban list poller.</summary>
        public static void DrainPendingLogs()
        {
            while (pendingLogs.TryDequeue(out var entry))
            {
                switch (entry.Item1)
                {
                    case Level.Error:
                        LogManager.LogError(entry.Item2);
                        break;
                    case Level.Warning:
                        LogManager.LogWarning(entry.Item2);
                        break;
                    default:
                        LogManager.Log(entry.Item2);
                        break;
                }
            }
        }

        static URLBlocker()
        {
            // LoadBanList reads PluginInfo on its first line, so a fault in PluginInfo's own type
            // initializer used to throw out of here and poison this type as well, taking the whole
            // ban list poller down instead of just its first line. Guarded so a failure degrades to
            // "the poller is not running" instead of every other member here throwing too.
            try { LoadBanList(); }
            catch (System.Exception e) { LogManager.LogError("The ban list poller could not be started: " + e.Message); }
        }

        /// <summary>
        /// Refreshes the ban list, retrying every 30 seconds.
        ///
        /// Two behaviours here used to fail open. An empty or malformed payload replaced the live
        /// list with an empty one, so a bad response from the server silently turned every
        /// protection off until the next good poll. And every failure was swallowed by a bare
        /// catch, so a blocker that had been dead for hours looked identical to a healthy one.
        /// </summary>
        private static async void LoadBanList()
        {
            if (!PluginInfo.Online)
                return;

            bool reportedFailure = false;

            while (true)
            {
                try
                {
                    {
                        string json = await httpClient.GetStringAsync($"{PluginInfo.ApiBase}/banned_urls");
                        var parsed = JsonConvert.DeserializeObject<BanResponse>(json);

                        if (parsed?.banned != null && parsed.banned.Count > 0)
                        {
                            lock (locker)
                            {
                                banned = parsed.banned;
                            }

                            lastSuccessfulLoad = uptime.Elapsed.TotalSeconds;
                            reportedFailure = false;
                            stalenessWarned = false;

                            QueueLog(Level.Info, $"URLBlocker loaded {parsed.banned.Count} banned URL entr{(parsed.banned.Count == 1 ? "y" : "ies")}.");
                        }
                        else if (parsed?.banned != null && parsed.banned.Count == 0)
                        {
                            // Keep whatever is already loaded. An empty list is far more likely to
                            // be a truncated or placeholder response than a real instruction to
                            // stop blocking everything.
                            WarnStaleOnce("the server returned an empty ban list, so the previously loaded list is being kept");
                        }
                        else
                        {
                            WarnStaleOnce("the server response could not be parsed as a ban list, so the previously loaded list is being kept");
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (!reportedFailure)
                    {
                        QueueLog(Level.Error, $"URLBlocker could not refresh the ban list: {exception.Message}");
                        reportedFailure = true;
                    }

                    // Without this, an endpoint that is simply unreachable reports one line and then
                    // goes quiet forever, because the stale check below is only reachable from the
                    // paths that got an HTTP response. A dead blocker and a healthy one then look
                    // identical, which is the exact failure this class is meant to make visible.
                    WarnStaleOnce("the ban list could not be refreshed, so the previously loaded list is being kept");
                }

                await Task.Delay(30000);
            }
        }

        /// <summary>
        /// Surfaces a ban list that has not been successfully refreshed in a long time. Blocking is
        /// only as good as the list behind it, and this is the only signal that the list has gone
        /// stale rather than simply having nothing new to say.
        /// </summary>
        private static void WarnStaleOnce(string reason)
        {
            if (uptime.Elapsed.TotalSeconds - lastSuccessfulLoad < StaleAfterSeconds)
                return;

            if (stalenessWarned)
                return;

            stalenessWarned = true;

            int count;
            lock (locker) count = banned.Count;

            QueueLog(Level.Error, $"URLBlocker: {reason}. No successful refresh in over {StaleAfterSeconds / 60d} minutes, still enforcing {count} entr{(count == 1 ? "y" : "ies")}.");
        }

        [PatchHandler.SecurityPatch]
        private static void Notify(string url, string reason)
        {
            string assemblyName = "Unknown";
            string fileName = "Unknown";

            try
            {
                var stack = new StackTrace();

                for (int i = 0; i < stack.FrameCount; i++)
                {
                    var method = stack.GetFrame(i)?.GetMethod();
                    var asm = method?.DeclaringType?.Assembly;

                    if (asm == null)
                        continue;

                    string name = asm.GetName().Name;

                    // this is ugly
                    if (name.StartsWith("Unity") ||
                        name.StartsWith("System") ||
                        name.StartsWith("Mono") ||
                        name.StartsWith("mscorlib") ||
                        name.StartsWith("Harmony"))
                        continue;

                    assemblyName = name;

                    try
                    {
                        fileName = System.IO.Path.GetFileName(asm.Location);
                    }
                    catch { }

                    break;
                }
            }
            catch { }

            bool shouldLog;
            lock (notifyLocker)
                shouldLog = notifiedAssemblies.Add(assemblyName);

            if (shouldLog)
                LogManager.Log($"HEY!! Ratalyth Menu blocked a potentionally DANGEROUS REQUEST to: {url} | Reason: {reason} | Assumed Assembly: {assemblyName} | Assumed File: {fileName}");
        }

        private static string NormalizeHost(string host)
        {
            if (string.IsNullOrEmpty(host))
                return host;

            string result = host;

            try
            {
                var idn = new IdnMapping();
                result = idn.GetAscii(result);
            }
            catch { }

            return result.ToLowerInvariant();
        }

        private static bool IsBanned(string url, out string reason)
        {
            reason = null;

            if (string.IsNullOrEmpty(url))
                return false;

            try
            {
                Uri uri = new Uri(url);
                string host = NormalizeHost(uri.Host);

                Dictionary<string, string> snapshot;

                lock (locker)
                {
                    snapshot = banned;
                }

                foreach (var entry in snapshot)
                {
                    string key = NormalizeHost(entry.Key);

                    if (host == key || host.EndsWith("." + key))
                    {
                        reason = entry.Value;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static List<string> ExtractUrls(string input)
        {
            var results = new List<string>();

            if (string.IsNullOrEmpty(input))
                return results;

            string[] parts = input.Split(new[] { ' ', '\t', '\n', '\r', '"', '\'', '(', ')', '[', ']', '^' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                string cleaned = part.Trim('"', '\'');

                if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(cleaned);
                }
            }

            return results;
        }

        private static bool IsBase64String(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length % 4 != 0)
                return false;

            foreach (char c in s)
            {
                if (!(char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='))
                    return false;
            }

            return true;
        }

        private static List<string> ExtractAndDecodeBase64(string input)
        {
            var results = new List<string>();

            if (string.IsNullOrEmpty(input))
                return results;

            string[] parts = input.Split(' ');

            foreach (var part in parts)
            {
                string cleaned = part.Trim('"');

                if (cleaned.Length > 20 && IsBase64String(cleaned))
                {
                    try
                    {
                        byte[] data = Convert.FromBase64String(cleaned);

                        string unicode = System.Text.Encoding.Unicode.GetString(data);
                        results.Add(unicode);

                        string utf8 = System.Text.Encoding.UTF8.GetString(data);
                        if (utf8 != unicode)
                            results.Add(utf8);
                    }
                    catch { }
                }
            }

            return results;
        }

        private static bool IsBlockedProcess(string args, IEnumerable<string> argList = null)
        {
            bool Check(string text)
            {
                if (string.IsNullOrEmpty(text))
                    return false;

                var urls = ExtractUrls(text);
                foreach (var url in urls)
                {
                    if (IsBanned(url, out var reason))
                    {
                        Notify(url, reason);
                        return true;
                    }
                }

                var decodedStrings = ExtractAndDecodeBase64(text);
                foreach (var decoded in decodedStrings)
                {
                    var innerUrls = ExtractUrls(decoded);

                    foreach (var url in innerUrls)
                    {
                        if (IsBanned(url, out var reason))
                        {
                            Notify(url, reason);
                            return true;
                        }
                    }
                }

                return false;
            }

            if (Check(args))
                return true;

            if (argList != null)
            {
                foreach (var arg in argList)
                {
                    if (Check(arg))
                        return true;
                }
            }

            return false;
        }

        private class BanResponse
        {
            public Dictionary<string, string> banned = new Dictionary<string, string>();
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(UnityWebRequest), "SendWebRequest")]
        private class Patch_UnityWebRequest
        {
            static bool Prefix(UnityWebRequest __instance)
            {
                if (IsBanned(__instance.url, out var reason))
                {
                    Notify(__instance.url, reason);
                    __instance.Abort();
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(HttpClient), "SendAsync", new[] { typeof(HttpRequestMessage), typeof(CancellationToken) })]
        private class Patch_HttpClient
        {
            static bool Prefix(HttpRequestMessage request, ref Task<HttpResponseMessage> __result)
            {
                if (request?.RequestUri != null &&
                    IsBanned(request.RequestUri.ToString(), out var reason))
                {
                    Notify(request.RequestUri.ToString(), reason);

                    var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("This request has been blocked by Ratalyth Menu, as it has been marked as a unsafe site.")
                    };

                    __result = Task.FromResult(response);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebRequest), "Create", new[] { typeof(string) })]
        private class Patch_WebRequest
        {
            static bool Prefix(string requestUriString, ref WebRequest __result)
            {
                if (IsBanned(requestUriString, out var reason))
                {
                    Notify(requestUriString, reason);
                    __result = null;
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(Process), "Start", new[] { typeof(string), typeof(string) })]
        private class Patch_ProcessStart_String
        {
            static bool Prefix(string fileName, string arguments)
            {
                if (IsBlockedProcess(arguments))
                {
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(Process), "Start", new[] { typeof(ProcessStartInfo) })]
        private class Patch_ProcessStart_Info
        {
            static bool Prefix(ProcessStartInfo startInfo)
            {
                if (startInfo != null && IsBlockedProcess(startInfo.Arguments, startInfo.ArgumentList))
                {
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "DownloadString", new[] { typeof(string) })]
        private class Patch_WebClient_DownloadString_String
        {
            static bool Prefix(string address, ref string __result)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    __result = string.Empty;
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "DownloadString", new[] { typeof(Uri) })]
        private class Patch_WebClient_DownloadString_Uri
        {
            static bool Prefix(Uri address, ref string __result)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    __result = string.Empty;
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "DownloadFile", new[] { typeof(string), typeof(string) })]
        private class Patch_WebClient_DownloadFile_String
        {
            static bool Prefix(string address, string fileName)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "DownloadFile", new[] { typeof(Uri), typeof(string) })]
        private class Patch_WebClient_DownloadFile_Uri
        {
            static bool Prefix(Uri address, string fileName)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "OpenRead", new[] { typeof(string) })]
        private class Patch_WebClient_OpenRead_String
        {
            static bool Prefix(string address, ref System.IO.Stream __result)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    __result = null;
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "OpenRead", new[] { typeof(Uri) })]
        private class Patch_WebClient_OpenRead_Uri
        {
            static bool Prefix(Uri address, ref System.IO.Stream __result)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    __result = null;
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "DownloadData", new[] { typeof(string) })]
        private class Patch_WebClient_DownloadData_String
        {
            static bool Prefix(string address)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "DownloadData", new[] { typeof(Uri) })]
        private class Patch_WebClient_DownloadData_Uri
        {
            static bool Prefix(Uri address)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadString", new[] { typeof(string), typeof(string) })]
        private class Patch_WebClient_UploadString_String
        {
            static bool Prefix(string address, ref string __result)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    __result = string.Empty;
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadString", new[] { typeof(Uri), typeof(string) })]
        private class Patch_WebClient_UploadString_Uri
        {
            static bool Prefix(Uri address, ref string __result)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    __result = string.Empty;
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadData", new[] { typeof(string), typeof(byte[]) })]
        private class Patch_WebClient_UploadData_String
        {
            static bool Prefix(string address)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadData", new[] { typeof(Uri), typeof(byte[]) })]
        private class Patch_WebClient_UploadData_Uri
        {
            static bool Prefix(Uri address)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadFile", new[] { typeof(string), typeof(string) })]
        private class Patch_WebClient_UploadFile_String
        {
            static bool Prefix(string address)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadFile", new[] { typeof(Uri), typeof(string) })]
        private class Patch_WebClient_UploadFile_Uri
        {
            static bool Prefix(Uri address)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadValues", new[] { typeof(string), typeof(System.Collections.Specialized.NameValueCollection) })]
        private class Patch_WebClient_UploadValues_String
        {
            static bool Prefix(string address)
            {
                if (IsBanned(address, out var reason))
                {
                    Notify(address, reason);
                    return false;
                }
                return true;
            }
        }

        [PatchHandler.SecurityPatch]
        [HarmonyPatch(typeof(WebClient), "UploadValues", new[] { typeof(Uri), typeof(System.Collections.Specialized.NameValueCollection) })]
        private class Patch_WebClient_UploadValues_Uri
        {
            static bool Prefix(Uri address)
            {
                if (address != null && IsBanned(address.ToString(), out var reason))
                {
                    Notify(address.ToString(), reason);
                    return false;
                }
                return true;
            }
        }
    }
}