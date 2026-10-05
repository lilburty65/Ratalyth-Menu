/*
 * Ratalyth Menu  Classes/Menu/ServerData.cs
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

using GorillaNetworking;
using MonoMod.Utils;
using Photon.Pun;
using Photon.Realtime;
using Ratalyth.Extensions;
using Ratalyth.Managers;
using Ratalyth.Menu;
using Ratalyth.Mods;
using Ratalyth.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Valve.Newtonsoft.Json;
using Valve.Newtonsoft.Json.Linq;

namespace Ratalyth.Classes.Menu
{
    public class ServerData : MonoBehaviour
    {
        #region Configuration
        public static readonly bool ServerDataEnabled = true; // Disables Console and admin panel
        public static bool DisableTelemetry = false; // Disables telemetry data being sent to the server

        // Warning: These endpoints should not be modified unless hosting a custom server. Use with caution.
        public const string ServerEndpoint = PluginInfo.ApiBase;
        public static readonly string ServerDataEndpoint = $"{ServerEndpoint}/serverdata";

        // Do not change this unless you are hosting unofficial files for Console
        public const string AssetURL = "https://raw.githubusercontent.com/Seralyth/Console/refs/heads/master/ServerData";

        // Admin lists are plain text so they can be updated without rebuilding or redeploying the
        // menu. These are fetched after the API and independently of it, so admin still resolves
        // when the API is unreachable. One entry per line, '#' begins a comment, and an optional
        // ",name" suffix sets the display name used by the admin panel and nametag crown.
        public const string ConsoleAdminsURL = "https://raw.githubusercontent.com/lilburty65/Ratalyth-Console/refs/heads/main/Admin.json";
        public const string ConsoleSuperAdminsURL = "https://raw.githubusercontent.com/lilburty65/Ratalyth-Console/refs/heads/main/SuperAdmin.json";

        // The dictionary used to assign the admins only seen in your mod. Seralyth keeps their own
        // admins hardcoded here for exactly this reason: the panel is granted from a dict baked into
        // the build, so it does not depend on any request succeeding. The key is the PlayFab id,
        // which survives rejoins; TryGetLocalAdministrator also accepts a Photon user id so a session
        // still resolves while PlayFab is still warming up.
        public static readonly Dictionary<string, string> LocalAdmins = new Dictionary<string, string>()
        {
        };

        // Local super admins, merged into both admin tables. Kept separate from LocalAdmins so
        // LoadServerData can re-apply it after it clears SuperAdministrators.
        public static readonly Dictionary<string, string> LocalSuperAdmins = new Dictionary<string, string>()
        {
        };

        public static void SetupAdminPanel(string playername) => // Method used to spawn admin panel
            Main.SetupAdminPanel(playername);
        #endregion

        #region Server Data Code
        private static ServerData instance;

        private static readonly List<string> DetectedModsLabelled = new List<string>();

        private static float DataLoadTime = -1f;
        private static float ReloadTime = -1f;

        private static int LoadAttempts;

        // Poll interval for the parts of Update that do not need to run at frame rate. The admin
        // grant only becomes true once the button tables or the PlayFab id land, and the roster
        // sync only cares whether the player count changed.
        private const float PollInterval = 0.25f;
        private static float nextPollTime;

        private static bool cachedLocalSuperAdmin;
        private static bool cachedLocalSuperAdminValid;

        private static bool BetaBuildWarning;
        public static bool OutdatedVersion;

        private static bool GivenAdminMods;
        private static bool GivenPateronMods;

        public void Awake()
        {
            instance = this;
            DataLoadTime = Time.time + 5f;

            LoadLocalAdmins();

            NetworkSystem.Instance.OnJoinedRoomEvent += OnJoinRoom;

            NetworkSystem.Instance.OnPlayerJoined += UpdatePlayerCount;
            NetworkSystem.Instance.OnPlayerLeft += UpdatePlayerCount;
        }

        /// <summary>
        /// Seeds Administrators and SuperAdministrators from the hardcoded lists. Upstream only
        /// applied LocalAdmins inside the server data parse, so a failed request left even the
        /// build's own admins without a panel. Running it here makes the local list independent of
        /// the network, and it is idempotent so it can also be re-run after the API clears the tables.
        /// </summary>
        private static void LoadLocalAdmins()
        {
            foreach (KeyValuePair<string, string> entry in LocalAdmins)
            {
                if (!Administrators.ContainsKey(entry.Key))
                    Administrators[entry.Key] = entry.Value;
            }

            foreach (KeyValuePair<string, string> entry in LocalSuperAdmins)
            {
                if (!Administrators.ContainsKey(entry.Key))
                    Administrators[entry.Key] = entry.Value;

                if (!SuperAdministrators.Contains(entry.Key))
                    SuperAdministrators.Add(entry.Key);
            }

            InvalidateAdminCache();
        }

        /// <summary>
        /// Puts the last successfully loaded GitHub lists back into Administrators.
        ///
        /// LoadServerData rebuilds Administrators from scratch, and the two GitHub lists are only
        /// merged back in by the fetches that run after it, so one failed request on a pass dropped
        /// the player out of the admin table until the next 30 second reload. RemoteAdmins and
        /// RemoteSuperAdmins are only cleared once a fetch succeeds, so they still hold the last
        /// list that worked.
        /// </summary>
        private static void ReapplyRemoteAdmins()
        {
            int restored = 0;

            foreach (string entry in RemoteAdmins)
            {
                if (Administrators.ContainsKey(entry)) continue;

                Administrators[entry] = entry;
                restored++;
            }

            foreach (KeyValuePair<string, string> entry in RemoteSuperAdmins)
            {
                if (!Administrators.ContainsKey(entry.Key))
                {
                    Administrators[entry.Key] = entry.Value;
                    restored++;
                }

                if (!SuperAdministrators.Contains(entry.Key))
                    SuperAdministrators.Add(entry.Key);
            }

            if (restored > 0)
                Console.Log($"Remote admin list could not be refreshed, restored {restored} known admin entr{(restored == 1 ? "y" : "ies")} from the last successful load.");

            InvalidateAdminCache();
        }

        public void Update()
        {
            if (DataLoadTime > 0f && Time.time > DataLoadTime && PluginInfo.Online &&
                GorillaComputer.instance != null && GorillaComputer.instance.isConnectedToMaster)
            {
                DataLoadTime = Time.time + 5f;

                LoadAttempts++;
                if (LoadAttempts >= 3)
                {
                    Console.Log("Server data could not be loaded");
                    DataLoadTime = -1f;
                    // Deliberately no return here. This used to bail out of Update entirely,
                    // which also stopped the admin panel retry and the roster sync below for the
                    // rest of the session just because the server data request failed.
                }
                else
                {
                    Console.Log("Attempting to load web data");
                    instance.StartCoroutine(RefreshServerData());
                }
            }

            if (ReloadTime > 0f)
            {
                if (Time.time > ReloadTime)
                {
                    ReloadTime = Time.time + 30f;
                    instance.StartCoroutine(RefreshServerData());
                }
            }
            else
            {
                if (GorillaComputer.instance != null && GorillaComputer.instance.isConnectedToMaster)
                    ReloadTime = Time.time + 5f;
            }

            if (!GivenAdminMods)
                TryGiveAdminPanel();

            if (Time.time < nextPollTime)
                return;

            nextPollTime = Time.time + PollInterval;

            // Keep retrying the grant until it lands. PlayFab id and the button tables are both
            // populated asynchronously, so a single check that runs before either is ready fails
            // silently and used to leave the panel missing for the rest of the session.

            if (!(Time.time > DataSyncDelay) && NetworkSystem.Instance.InRoom) return;
            if (NetworkSystem.Instance.InRoom && PhotonNetwork.PlayerList.Length != PlayerCount)
            {
                instance.StartCoroutine(PlayerDataSync(PhotonNetwork.CurrentRoom.Name, PhotonNetwork.CloudRegion));
            }

            PlayerCount = NetworkSystem.Instance.InRoom ? PhotonNetwork.PlayerList.Length : -1;
        }

        private IEnumerator RefreshServerData()
        {
            yield return LoadServerData();
            yield return RefreshConsoleAdmins();
            yield return RefreshConsoleSuperAdmins();

            // Every path above can mutate Administrators or SuperAdministrators, and the per-frame
            // consumers read a cached super admin result, so drop it once here rather than at each
            // individual mutation site.
            InvalidateAdminCache();

            List<string> notIdentifiers = ValidateSuperAdminList();
            if (notIdentifiers.Count > 0)
                Console.Log($"Warning: {notIdentifiers.Count} super admin entr{(notIdentifiers.Count == 1 ? "y is" : "ies are")} not a 16-character PlayFab id. Those entries can only match the Photon user id, which is regenerated every session, so they grant super admin at most until the next rejoin. Update SuperAdmin.json to use PlayFab ids: {string.Join(", ", notIdentifiers)}");

            TryGiveAdminPanel();
        }

        public static void OnJoinRoom()
        {
            instance.StartCoroutine(TelemetryRequest(PhotonNetwork.CurrentRoom.Name, PhotonNetwork.NickName, PhotonNetwork.CloudRegion, PhotonNetwork.LocalPlayer.UserId, PhotonNetwork.CurrentRoom.IsVisible, PhotonNetwork.PlayerList.Length, NetworkSystem.Instance.GameModeString));
            TryGiveAdminPanel();
        }

        public static string CleanString(string input, int maxLength = 12)
        {
            input = new string(Array.FindAll(input.ToCharArray(), Utils.IsASCIILetterOrDigit));

            if (input.Length > maxLength)
                input = input[..(maxLength - 1)];

            input = input.ToUpper();
            return input;
        }

        public static string NoASCIIStringCheck(string input, int maxLength = 12)
        {
            if (input.Length > maxLength)
                input = input[..(maxLength - 1)];

            input = input.ToUpper();
            return input;
        }

        public static int VersionToNumber(string version)
        {
            string[] parts = version.Split('.');
            if (parts.Length != 3)
                return -1; // Version must be in 'major.minor.patch' format

            return int.Parse(parts[0]) * 100 + int.Parse(parts[1]) * 10 + int.Parse(parts[2]);
        }

        public static readonly Dictionary<string, string> Administrators = new Dictionary<string, string>();
        public static readonly List<string> SuperAdministrators = new List<string>();

        public static readonly HashSet<string> RemoteAdmins = new HashSet<string>();
        public static readonly Dictionary<string, string> RemoteSuperAdmins = new Dictionary<string, string>();

        /// <summary>
        /// Super admins are matched on stable identifiers only. A display name can be changed by
        /// anyone at any time, so matching on one let any player rename themselves into the admin
        /// panel and inherit every super-admin command, including game-setposition,
        /// game-setrotation and game-clone.
        ///
        /// The PlayFab id is the only identifier that survives a rejoin and cannot be forged, so
        /// it is the one that counts. The Photon user id is still accepted because that is what the
        /// legacy lists were built from. Names are no longer accepted at all;
        /// <see cref="ValidateSuperAdminList"/> reports entries that still look like display names
        /// so the source list can be corrected instead of quietly granting nothing.
        /// </summary>
        public static bool IsSuperAdmin(string userId = null, string playFabId = null)
        {
            if (SuperAdministrators.Count == 0)
                return false;

            foreach (string entry in SuperAdministrators)
            {
                if (!string.IsNullOrEmpty(playFabId) && string.Equals(entry, playFabId, StringComparison.Ordinal))
                    return true;
                if (!string.IsNullOrEmpty(userId) && string.Equals(entry, userId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>Super admin status for any player, using whichever ids we know about them.</summary>
        public static bool IsSuperAdmin(Player player)
        {
            if (player == null)
                return false;

            return IsSuperAdmin(player.UserId, PlayFabIdFor(player));
        }

        /// <summary>
        /// A PlayFab id is 16 hexadecimal characters. Any other entry in the super admin list is a
        /// display name, which no longer grants anything, so it is reported rather than silently
        /// ignored.
        /// </summary>
        public static List<string> ValidateSuperAdminList()
        {
            List<string> notIdentifiers = new List<string>();

            foreach (string entry in SuperAdministrators)
            {
                if (IsPlayFabId(entry)) continue;

                notIdentifiers.Add(entry);
            }

            return notIdentifiers;
        }

        private static bool IsPlayFabId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 16)
                return false;

            foreach (char c in value)
            {
                bool isHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

                if (!isHexDigit) return false;
            }

            return true;
        }

        /// <summary>
        /// Cached form of <see cref="IsLocalSuperAdmin"/> for per-frame callers. Super admin status
        /// only changes when the admin tables are reloaded, so re-resolving it every frame, which
        /// walks the super admin list doing string comparisons, is wasted work.
        /// </summary>
        public static bool IsLocalSuperAdminCached()
        {
            if (!cachedLocalSuperAdminValid)
            {
                cachedLocalSuperAdmin = IsLocalSuperAdmin();
                cachedLocalSuperAdminValid = true;
            }

            return cachedLocalSuperAdmin;
        }

        /// <summary>
        /// Drops the cached super admin result. Must be called whenever Administrators or
        /// SuperAdministrators are mutated, or a granted or revoked admin keeps the stale answer.
        /// </summary>
        public static void InvalidateAdminCache()
        {
            cachedLocalSuperAdminValid = false;
        }

        /// <summary>
        /// The PlayFab id is only readable for players already in the room, so it is best effort.
        /// </summary>
        public static string PlayFabIdFor(Player player)
        {
            try
            {
                // PlayFabAuthenticator only ever reports the local player, so this is
                // gated on identity rather than trusted blindly.
                if (player != null && PhotonNetwork.LocalPlayer != null &&
                    string.Equals(player.UserId, PhotonNetwork.LocalPlayer.UserId, StringComparison.Ordinal) &&
                    PlayFabAuthenticator.instance != null)
                    return PlayFabAuthenticator.instance.GetPlayFabPlayerId();
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Super admin status for whoever is running this client.
        /// </summary>
        /// <param name="adminName">
        /// Kept so existing callers can keep passing the resolved display name. It no longer
        /// influences the result, because a name is not a valid grant path.
        /// </param>
        public static bool IsLocalSuperAdmin(string adminName = null)
        {
            string userId = PhotonNetwork.LocalPlayer?.UserId;

            if (!TryGetLocalAdministrator(out _))
                return false;

            return IsSuperAdmin(userId, PlayFabIdFor(PhotonNetwork.LocalPlayer));
        }
        public static IEnumerator LoadServerData()
        {
            using (UnityWebRequest request = UnityWebRequest.Get(ServerDataEndpoint))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Console.Log($"Failed to load server data:\nError: {request.error}\nResult: {request.result}\nResponse Code: {request.responseCode}\nBody (if any): {request.downloadHandler?.text}");
                    yield break;
                }

                string json = request.downloadHandler.text;
                DataLoadTime = -1f;

                // The attempt counter is only meant to stop an endless retry loop against a
                // server that is down. It used to never reset, so three transient failures at
                // startup permanently disabled server data for the rest of the session even
                // though later requests would have succeeded.
                LoadAttempts = 0;

                JObject data = JObject.Parse(json);

                Main.serverLink = (string)data["discord-invite"];
                if (CustomBoardManager.motdTemplate != (string)data["motd"])
                {
                    CustomBoardManager.motdTextDirty = true;
                    CustomBoardManager.motdTemplate = (string)data["motd"];
                }

                // Version Check
                string minimumVersion = (string)data["min-version"];
                string version = (string)data["menu-version"];

                if (PluginInfo.BetaBuild)
                {
                    if (!BetaBuildWarning)
                    {
                        BetaBuildWarning = true;
                        Console.Log("User is on beta build");
                        Console.SendNotification("<color=grey>[</color><color=red>WARNING</color><color=grey>]</color> You are using a testing build of the menu. Be warned that there may be bugs and issues that could cause crashes, data loss, or other unexpected behavior.", 10000);
                    }
                }
                else if (VersionToNumber(PluginInfo.Version) < VersionToNumber(minimumVersion))
                {
                    if (!OutdatedVersion)
                    {
                        OutdatedVersion = true;
                        Console.Log("Version is severely outdated");
                        GorillaComputer.instance.GeneralFailureMessage("Please update your menu. For safety purposes, you have been blocked from joining rooms.");
                        if (NetworkSystem.Instance.InRoom)
                            NetworkSystem.Instance.ReturnToSinglePlayer();
                        Console.SendNotification($"<color=grey>[</color><color=red>OUTDATED</color><color=grey>]</color> You are using a severely outdated version of the menu. Please update your menu if available. For safety purposes, you have been blocked from joining rooms.", 10000);
                        // Our own version, not the server's. The prompt exists to tell the player
                        // what they are running, and passing the server's version back told them
                        // the opposite of what was wrong.
                        Main.UpdatePrompt(PluginInfo.Version);
                    }
                }
                else if (VersionToNumber(version) > VersionToNumber(PluginInfo.Version))
                {
                    if (!OutdatedVersion)
                    {
                        OutdatedVersion = true;
                        Console.Log("Version is outdated");
                        Console.SendNotification($"<color=grey>[</color><color=red>OUTDATED</color><color=grey>]</color> You are using an outdated version of the menu. Please update to version {version}.", 10000);
                        Main.UpdatePrompt(version);
                    }
                }

                

                string minConsoleVersion = (string)data["min-console-version"];
                if (VersionToNumber(Console.ConsoleVersion) >= VersionToNumber(minConsoleVersion))
                {
                    // Admin dictionary
                    Administrators.Clear();

                    JArray admins = (JArray)data["admins"];
                    foreach (var admin in admins)
                    {
                        string name = admin["name"].ToString();
                        string userId = admin["user-id"].ToString();
                        Administrators[userId] = name;
                    }

                    Administrators.AddRange(LocalAdmins);

                    SuperAdministrators.Clear();

                    JArray superAdmins = (JArray)data["super-admins"];
                    foreach (var superAdmin in superAdmins)
                        SuperAdministrators.Add(superAdmin.ToString());

                    // SuperAdministrators was just cleared, so re-apply the hardcoded entries.
                    LoadLocalAdmins();

                    }
                else
                    Console.Log("On extreme outdated version of Console, not loading administrators");

                // Patreon members
                if (PatreonManager.instance != null)
                {
                    PatreonManager.instance.PatreonMembers.Clear();

                    JArray members = (JArray)data["patreon"];
                    foreach (var member in members)
                        PatreonManager.instance.PatreonMembers.Add(member["user-id"].ToString(), new PatreonManager.PatreonMembership(member["name"].ToString(), member["photo"].ToString()));

                    // Give patreon if on list
                    string patreonUserId = PhotonNetwork.LocalPlayer?.UserId;
                    if (!GivenPateronMods && patreonUserId != null && PatreonManager.instance.PatreonMembers.TryGetValue(patreonUserId, out var membership))
                    {
                        GivenPateronMods = true;
                        PatreonManager.SetupPatreonMods(membership.TierName);
                    }
                }

                // Detected mod labels   
                JArray detectedMods = (JArray)data["detected-mods"];
                foreach (var detectedMod in detectedMods)
                {
                    string detectedModName = detectedMod.ToString();
                    if (DetectedModsLabelled.Contains(detectedModName)) continue;
                    ButtonInfo button = Buttons.GetIndex(detectedModName);
                    if (button != null)
                    {
                        string overlapText = button.overlapText ?? button.buttonText;

                        button.overlapText = overlapText + " <color=grey>[</color><color=red>Disabled</color><color=grey>]</color>";
                        string identityKey = PhotonNetwork.LocalPlayer?.UserId;
                        if (string.IsNullOrEmpty(identityKey))
                            identityKey = SafeLocalPlayFabId();

                        if (!string.IsNullOrEmpty(identityKey) && !Administrators.TryGetValue(identityKey, out _))
                        {
                            button.isTogglable = false;
                            button.SetEnabled(false);

                            button.method = delegate { Console.SendNotification("<color=grey>[</color><color=red>ERROR</color><color=grey>]</color> This mod is currently disabled, as it is detected."); };
                            button.enableMethod = button.method;
                            button.disableMethod = button.method;
                        }
                    }
                    DetectedModsLabelled.Add(detectedModName);
                }

                // April Fools
                JObject aprilFools = (JObject)data["april_fools"];
                foreach (var prop in aprilFools.Properties())
                {
                    if ((bool)prop.Value)
                    {
                        string modName = prop.Name;

                        if (prop.Name == "sex")
                        {
                            List<ButtonInfo> buttons = Buttons.buttons[Buttons.GetCategory("Main")].ToList();

                            if ((bool)prop.Value)
                            {
                                if (!buttons.Any(b => b.buttonText == "Sex"))
                                {
                                    buttons.Add(new ButtonInfo
                                    {
                                        buttonText = "Sex",
                                        method = Movement.PromptForSex,
                                        isTogglable = false,
                                        toolTip = "Sex"
                                    });
                                    AssetUtilities.LoadSoundFromURL($"{PluginInfo.ServerResourcePath}/Audio/Menu/achievement.ogg", "Audio/Menu/achievement.ogg", clip => clip.Play(Main.buttonClickVolume / 10f));
                                    NotificationManager.SendNotification($"<color=grey>[</color><color=#FFC0CB>SEX</color><color=grey>]</color> Sex mods have been enabled. Check the main page.", 10000);
                                }
                            }
                            else
                                if (Buttons.GetIndex("Sex") != null)
                            {
                                Buttons.buttons[Buttons.GetCategory("Main")] = buttons.Where(b => b.buttonText != "Sex").ToArray();
                            }

                            Buttons.buttons[Buttons.GetCategory("Main")] = buttons.ToArray();
                        }
                        Main.annoyingMode = prop.Name == "annoying" && (bool)prop.Value;
                    }
                }
            }

            yield return null;
        }

        /// <summary>
        /// Grants the admin panel once every admin source has been consulted. This deliberately
        /// sits outside the API parse: the grant used to be nested inside the API block, so it was
        /// skipped whenever the API was unreachable and recognised admins were left without a panel.
        /// </summary>
        private static void TryGiveAdminPanel()
        {
            if (GivenAdminMods)
                return;

            if (!TryGetLocalAdministrator(out string administrator))
            {
                // A grant that never happens used to be completely silent. Say why, once, so a
                // player whose id is on the list can tell an unresolved PlayFab id apart from an
                // id that simply is not in the table.
                if (!loggedAdminMismatch && Time.time > DataLoadTime)
                {
                    loggedAdminMismatch = true;

                    string userId = PhotonNetwork.LocalPlayer?.UserId;
                    string playFabId = PlayFabIdFor(PhotonNetwork.LocalPlayer);
                    if (string.IsNullOrEmpty(playFabId))
                        playFabId = SafeLocalPlayFabId();

                    Console.Log($"Admin panel not granted. Your Photon user id is '{userId ?? "<null>"}' and your PlayFab id is '{playFabId ?? "<null>"}'. SuperAdmin.json and Admin.json entries are matched against the PlayFab id, or against the Photon user id for the current session only, so a Photon id from an earlier session will not match.");
                }

                return;
            }

            loggedAdminMismatch = false;

            // SetupAdminPanel indexes Buttons.buttons, which does not exist until the menu finishes
            // building its categories. GivenAdminMods is only set once the call actually succeeds so
            // the Update retry can pick the grant back up instead of giving up for the session.
            try
            {
                SetupAdminPanel(administrator);
                GivenAdminMods = true;
                Console.Log($"Admin panel granted to {administrator}.");
            }
            catch (Exception exception)
            {
                Console.Log($"Admin panel deferred: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static bool loggedAdminMismatch;

        /// <summary>
        /// Resolves the local player's admin display name. Remote lists are often keyed by PlayFab
        /// id rather than Photon user id, and those two are not interchangeable: Photon user ids
        /// rotate on every session while the PlayFab id survives. Matching only on the user id meant
        /// a PlayFab-keyed entry resolved to nothing, so both are checked here.
        /// </summary>
        public static bool TryGetLocalAdministrator(out string administrator)
        {
            administrator = null;

            string userId = PhotonNetwork.LocalPlayer?.UserId;

            string playFabId = PlayFabIdFor(PhotonNetwork.LocalPlayer);
            if (string.IsNullOrEmpty(playFabId))
                playFabId = SafeLocalPlayFabId();

            LogAdminIdentityOnce(userId, playFabId);

            if (!string.IsNullOrEmpty(userId) && Administrators.TryGetValue(userId, out administrator))
                return true;

            if (string.IsNullOrEmpty(playFabId))
                return false;

            if (RemoteSuperAdmins.TryGetValue(playFabId, out administrator))
                return true;

            return Administrators.TryGetValue(playFabId, out administrator);
        }

        /// <summary>
        /// PlayFabIdFor only answers when the local player can be identified through Photon, which
        /// is not guaranteed while the menu loads or while Photon is reconnecting. When asking
        /// specifically about the local player the authenticator can be queried directly, so this
        /// fallback skips the identity gate that would otherwise discard the answer.
        /// </summary>
        private static string SafeLocalPlayFabId()
        {
            try
            {
                return PlayFabAuthenticator.instance?.GetPlayFabPlayerId();
            }
            catch
            {
                return null;
            }
        }

        private static bool loggedAdminIdentity;

        private static void LogAdminIdentityOnce(string userId, string playFabId)
        {
            if (loggedAdminIdentity)
                return;

            loggedAdminIdentity = true;
            Console.Log($"Admin identity: userId={userId ?? "<null>"} playFab={playFabId ?? "<null>"} remoteAdmins=[{string.Join(",", RemoteAdmins)}] remoteSuperAdmins=[{string.Join(",", RemoteSuperAdmins.Keys)}]");
        }

        private static IEnumerable<string> SplitAdminList(string text)
        {
            foreach (string raw in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim().Trim('[', ']', '"', '{', '}', ',').Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                yield return line;
            }
        }

        private static bool TryParseAdminEntry(string line, out string userId, out string name)
        {
            userId = null;
            name = null;

            int separator = line.IndexOf(',');
            if (separator < 0)
            {
                userId = line;
                name = line;
                return userId.Length > 0;
            }

            userId = line.Substring(0, separator).Trim();
            name = line.Substring(separator + 1).Trim();
            if (name.Length == 0)
                name = userId;

            return userId.Length > 0;
        }

        public static IEnumerator RefreshConsoleAdmins()
        {
            string text = null;
            using (UnityWebRequest request = UnityWebRequest.Get(ConsoleAdminsURL))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                    text = request.downloadHandler.text;
                else
                    Console.Log($"Failed to load remote admin list: {request.error} (HTTP {request.responseCode})");
            }
            if (text == null)
            {
                ReapplyRemoteAdmins();
                yield break;
            }
            RemoteAdmins.Clear();
            string trimmed = text.Trim();
            bool parsedJson = false;
            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
            {
                try
                {
                    JToken token = JToken.Parse(trimmed);
                    JArray array = token as JArray;
                    if (array == null && token.Type == JTokenType.Object)
                        array = new JArray(token);
                    if (array != null)
                    {
                        foreach (JToken entryToken in array)
                        {
                            if (entryToken.Type != JTokenType.Object) continue;
                            JObject obj = (JObject)entryToken;
                            string userId = obj.Value<string>("user-id")
                                ?? obj.Value<string>("userid")
                                ?? obj.Value<string>("userId")
                                ?? obj.Value<string>("id")
                                ?? obj.Value<string>("UserId");
                            if (string.IsNullOrWhiteSpace(userId)) continue;
                            string name = obj.Value<string>("name") ?? obj.Value<string>("Name") ?? userId;
                            userId = userId.Trim(); name = name.Trim();
                            if (userId.Length == 0) continue;
                            RemoteAdmins.Add(userId);
                            if (!Administrators.ContainsKey(userId))
                                Administrators[userId] = name;
                        }
                        parsedJson = true;
                        Console.Log($"Loaded {RemoteAdmins.Count} admin(s) from remote list");
                    }
                }
                catch (Exception exception)
                {
                    Console.Log($"Failed to parse remote admin list JSON: {exception.Message}");
                }
            }
            if (parsedJson) yield break;
            foreach (string line in SplitAdminList(text))
            {
                if (!TryParseAdminEntry(line, out string userId, out string name)) continue;
                RemoteAdmins.Add(userId);
                if (!Administrators.ContainsKey(userId))
                    Administrators[userId] = name;
            }
            Console.Log($"Loaded {RemoteAdmins.Count} admin(s) from remote list");
}

        public static IEnumerator RefreshConsoleSuperAdmins()
        {
            string text = null;
            using (UnityWebRequest request = UnityWebRequest.Get(ConsoleSuperAdminsURL))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                    text = request.downloadHandler.text;
                else
                    Console.Log($"Failed to load remote super admin list: {request.error} (HTTP {request.responseCode})");
            }
            if (text == null)
            {
                ReapplyRemoteAdmins();
                yield break;
            }
            RemoteSuperAdmins.Clear();
            string trimmed = text.Trim();
            bool parsedJson = false;
            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
            {
                try
                {
                    JToken token = JToken.Parse(trimmed);
                    JArray array = token as JArray;
                    if (array == null && token.Type == JTokenType.Object)
                        array = new JArray(token);
                    if (array != null)
                    {
                        foreach (JToken entryToken in array)
                        {
                            if (entryToken.Type != JTokenType.Object) continue;
                            JObject obj = (JObject)entryToken;
                            string userId = obj.Value<string>("user-id")
                                ?? obj.Value<string>("userid")
                                ?? obj.Value<string>("userId")
                                ?? obj.Value<string>("id")
                                ?? obj.Value<string>("UserId");
                            if (string.IsNullOrWhiteSpace(userId)) continue;
                            string name = obj.Value<string>("name") ?? obj.Value<string>("Name") ?? userId;
                            userId = userId.Trim(); name = name.Trim();
                            if (userId.Length == 0) continue;
                            RemoteSuperAdmins[userId] = name;
                            if (!Administrators.ContainsKey(userId))
                                Administrators[userId] = name;
                            if (!SuperAdministrators.Contains(userId))
                                SuperAdministrators.Add(userId);
                        }
                        parsedJson = true;
                        Console.Log($"Loaded {RemoteSuperAdmins.Count} super admin(s) from remote list");
                    }
                }
                catch (Exception exception)
                {
                    Console.Log($"Failed to parse remote super admin list JSON: {exception.Message}");
                }
            }
            if (parsedJson) yield break;
            foreach (string line in SplitAdminList(text))
            {
                if (!TryParseAdminEntry(line, out string userId, out string name)) continue;
                RemoteSuperAdmins[userId] = name;
                if (!Administrators.ContainsKey(userId))
                    Administrators[userId] = name;
                if (!SuperAdministrators.Contains(userId))
                    SuperAdministrators.Add(userId);
            }
            Console.Log($"Loaded {RemoteSuperAdmins.Count} super admin(s) from remote list");
        }

        public static IEnumerator TelemetryRequest(string directory, string identity, string region, string userid, bool isPrivate, int playerCount, string gameMode)
        {
            if (DisableTelemetry)
                yield break;

            UnityWebRequest request = new UnityWebRequest(ServerEndpoint + "/telemetry", "POST");

            string json = JsonConvert.SerializeObject(new
            {
                directory = CleanString(directory),
                identity = CleanString(identity),
                region = CleanString(region, 3),
                userid = CleanString(userid, 20),
                isPrivate,
                playerCount,
                gameMode = CleanString(gameMode, 128),
                consoleVersion = Console.ConsoleVersion,
                menuName = Console.MenuName,
                menuVersion = Console.MenuVersion
            });

            byte[] raw = Encoding.UTF8.GetBytes(json);

            request.uploadHandler = new UploadHandlerRaw(raw);
            request.SetRequestHeader("Content-Type", "application/json");

            request.downloadHandler = new DownloadHandlerBuffer();
            yield return request.SendWebRequest();
        }

        private static float DataSyncDelay;
        public static int PlayerCount;

        public static void UpdatePlayerCount(NetPlayer Player)
        {
            PlayerCount = -1;
        }

        /// <summary>
        /// Platform as reported by the game for this rig.
        ///
        /// This carried its own copy of the old cosmetic-name scoring, including the "S. FIRST LOGIN"
        /// check that can no longer match anything. Delegating keeps the telemetry roster and the
        /// Steam detector from disagreeing with each other, which they did whenever one was patched
        /// and the other was not.
        /// </summary>
        public static bool IsPlayerSteam(VRRig Player) =>
            Player.IsSteam();

        public static IEnumerator PlayerDataSync(string directory, string region)
        {
            if (DisableTelemetry)
                yield break;

            DataSyncDelay = Time.time + 3f;
            yield return new WaitForSeconds(3f);

            if (!NetworkSystem.Instance.InRoom)
                yield break;

            Dictionary<string, Dictionary<string, string>> data = new Dictionary<string, Dictionary<string, string>>();

            foreach (Player identification in PhotonNetwork.PlayerList)
            {
                VRRig rig = Console.GetVRRigFromPlayer(identification) ?? VRRig.LocalRig;
                data.Add(identification.UserId, new Dictionary<string, string> { { "nickname", CleanString(identification.NickName) }, { "cosmetics", rig.Cosmetics() }, { "color", $"{Math.Round(rig.playerColor.r * 255)} {Math.Round(rig.playerColor.g * 255)} {Math.Round(rig.playerColor.b * 255)}" }, { "platform", IsPlayerSteam(rig) ? "STEAM" : "QUEST" } });
            }

            UnityWebRequest request = new UnityWebRequest(ServerEndpoint + "/syncdata", "POST");

            string json = JsonConvert.SerializeObject(new
            {
                directory = CleanString(directory),
                region = CleanString(region, 3),
                data
            });

            byte[] raw = Encoding.UTF8.GetBytes(json);

            request.uploadHandler = new UploadHandlerRaw(raw);
            request.SetRequestHeader("Content-Type", "application/json");

            request.downloadHandler = new DownloadHandlerBuffer();
            yield return request.SendWebRequest();
        }
        #endregion
    }
}
