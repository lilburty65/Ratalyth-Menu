/*
 * Ratalyth Menu  Mods/Preferences.cs
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

using Photon.Pun;
using Ratalyth.Managers;
using Ratalyth.Menu;
using Ratalyth.Mods;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Valve.Newtonsoft.Json;
using static Ratalyth.Menu.Main;

namespace Ratalyth.Classes.Menu
{
    public static class Preferences
    {
        private const string FileName = "Ratalyth_Preferences.json";
        private const string LegacyFileName = "Ratalyth_Preferences.txt";

        private const int MinWriteIntervalMs = 250;

        /// <summary>
        /// Bumped whenever the shape of <see cref="PreferencesData"/> changes in a way that older
        /// builds could not read. Load refuses to apply a file written by a newer build rather than
        /// silently dropping the fields it does not recognise and then overwriting the file.
        /// </summary>
        private const int CurrentSchemaVersion = 2;

        /// <summary>
        /// Versions older than this are read through <see cref="Migrate"/> before being applied.
        /// </summary>
        private const int OldestSupportedSchemaVersion = 1;

        public class SavedButtonState
        {
            public bool? enabled;
            public object value;
            public string rebindKey;
        }

        public class RgbColor
        {
            public byte r;
            public byte g;
            public byte b;

            public RgbColor() { }

            public RgbColor(Color c)
            {
                r = ToByte(c.r);
                g = ToByte(c.g);
                b = ToByte(c.b);
            }

            private static byte ToByte(float channel) =>
                (byte)Mathf.Clamp(Mathf.Round(channel * 255f), 0f, 255f);

            public Color32 ToColor32() => new Color32(r, g, b, 255);
        }

        public class CustomThemeData
        {
            public RgbColor backgroundFirst;
            public RgbColor backgroundSecond;
            public RgbColor buttonDisabledFirst;
            public RgbColor buttonDisabledSecond;
            public RgbColor buttonEnabledFirst;
            public RgbColor buttonEnabledSecond;
            public RgbColor textTitle;
            public RgbColor textDisabled;
            public RgbColor textEnabled;
        }

        public class PreferencesData
        {
            public int schemaVersion = CurrentSchemaVersion;
            public Dictionary<string, SavedButtonState> buttons = new Dictionary<string, SavedButtonState>();
            public List<string> favorites = new List<string>();
            public List<string> quickActions = new List<string>();
            public List<string> skipButtons = new List<string>();
            public Dictionary<string, List<string>> modBindings = new Dictionary<string, List<string>>();
            public Dictionary<string, string> sounds = new Dictionary<string, string>();
            public bool? disableLocalSoundboard;
            public string oldId;
            public CustomThemeData customTheme;
            public Dictionary<string, object> misc = new Dictionary<string, object>();
        }

        public static string JsonPath => Path.Combine(PluginInfo.BaseDirectory, FileName);
        public static string LegacyPath => Path.Combine(PluginInfo.BaseDirectory, LegacyFileName);

        private static PreferencesData _cache;

        private static readonly Stopwatch _writeClock = Stopwatch.StartNew();
        private static long _lastWriteMs = long.MinValue / 2;
        private static bool _writePending;

        /// <summary>
        /// True while preferences are actively being loaded/applied. Any code that needs
        /// to change a button's state should wrap that work in this flag via <see cref="RunWithoutSaving"/> so
        /// individual state changes don't get persisted mid-restore or wipe good data.
        /// </summary>
        public static bool IsApplyingPreferences { get; private set; }

        /// <summary>
        /// Runs <paramref name="action"/> with saves suppressed, then restores the previous
        /// suppression state. Use this if you need to change a button's state without saving it.
        /// </summary>
        public static void RunWithoutSaving(Action action)
        {
            if (action == null) return;

            bool previous = IsApplyingPreferences;
            IsApplyingPreferences = true;
            try { action(); }
            finally { IsApplyingPreferences = previous; }
        }

        private static PreferencesData BuildFullSnapshot()
        {
            var data = new PreferencesData();

            // Buttons.buttons is null until the menu finishes building its button list. Save can be
            // reached before that, and the resulting NullReferenceException used to abort the whole
            // snapshot, leaving _cache stale and quietly dropping every other setting too.
            if (Buttons.buttons != null)
            {
                foreach (ButtonInfo[] buttonList in Buttons.buttons)
                {
                    if (buttonList == null) continue;

                    foreach (ButtonInfo b in buttonList)
                    {
                        if (b == null) continue;
                        if (b.detected || b.excludeFromSave || b.label)
                            continue;

                        var state = ToSavedState(b);
                        if (state != null)
                            data.buttons[b.buttonText] = state;
                    }
                }
            }

            data.favorites = favorites.ToList();
            data.quickActions = quickActions.ToList();
            data.skipButtons = skipButtons.ToList();
            data.modBindings = ModBindings.ToDictionary(kv => kv.Key, kv => kv.Value);

            data.sounds["Button"] = SoundManager.DefaultSounds.TryGetValue("Button", out string btn) ? btn : "Default";
            data.sounds["Notification"] = SoundManager.DefaultSounds.TryGetValue("Notification", out string notif) ? notif : "None";
            data.disableLocalSoundboard = Sound.disableLocalSoundboard;
            data.customTheme = Settings.ExportCustomTheme();
            data.oldId = Important.oldId ?? "";

            data.misc["pageButtonType"] = pageButtonType;
            data.misc["themeType"] = themeType;
            data.misc["fontCycle"] = fontCycle;
            data.misc["pageSize"] = _pageSize;
            data.misc["playTime"] = (int)MathF.Ceiling(playTime);
            data.misc["userId"] = PhotonNetwork.LocalPlayer?.UserId ?? "null";

            return data;
        }

        private static SavedButtonState ToSavedState(ButtonInfo b)
        {
            bool hasValue = b.isSetting && b.value != null;
            bool hasRebind = !string.IsNullOrEmpty(b.rebindKey);
            bool hasEnabledInfo = b.isTogglable;

            if (!hasEnabledInfo && !hasValue && !hasRebind)
                return null;

            return new SavedButtonState
            {
                enabled = b.enabled,
                value = b.value,
                rebindKey = b.rebindKey
            };
        }

        private static void WriteToDisk(PreferencesData data)
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            WriteAllTextAtomic(JsonPath, json);

            _lastWriteMs = _writeClock.ElapsedMilliseconds;
            _writePending = false;
        }

        /// <summary>
        /// Writes through a temp file and an atomic replace.
        ///
        /// A plain WriteAllText leaves a half written file behind if the process dies, the disk
        /// fills, or the player yanks the cable mid write. Because there was no way to tell a
        /// truncated file from a corrupt one, that turned an ordinary crash into a total loss of
        /// every setting, and the player had no way back other than deleting the file by hand.
        ///
        /// The previous good file is kept as .bak so Load can fall back to it.
        /// </summary>
        private static void WriteAllTextAtomic(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string temp = path + ".tmp";

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }

            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            try
            {
                File.Replace(temp, path, path + ".bak", true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(path);
                File.Move(temp, path);
            }
            catch (IOException)
            {
                File.Delete(path);
                File.Move(temp, path);
            }
        }

        private static void FlushNow()
        {
            if (_cache == null) return;

            try { WriteToDisk(_cache); }
            catch (Exception e) { LogManager.Log("Error writing preferences: " + e.Message); }
        }

        private static void RequestWrite()
        {
            if (_cache == null) return;

            long elapsed = _writeClock.ElapsedMilliseconds - _lastWriteMs;
            if (elapsed >= MinWriteIntervalMs)
            {
                FlushNow();
            }
            else
            {
                _writePending = true;
            }
        }

        public static void FlushPendingWrites()
        {
            if (_writePending)
                FlushNow();
        }

        public static void Save()
        {
            if (IsApplyingPreferences) return;

            try
            {
                _cache = BuildFullSnapshot();
                FlushNow();
            }
            catch (Exception e) { LogManager.Log("Error saving preferences: " + e.Message); }
        }

        public static void SaveButton(ButtonInfo b)
        {
            if (IsApplyingPreferences) return;
            if (b == null || b.detected || b.excludeFromSave)
                return;

            try
            {
                _cache ??= BuildFullSnapshot();

                var state = ToSavedState(b);
                if (state != null)
                    _cache.buttons[b.buttonText] = state;
                else
                    _cache.buttons.Remove(b.buttonText);

                SyncMirroredSettings(_cache);

                RequestWrite();
            }
            catch (Exception e) { LogManager.Log($"Error saving button '{b.buttonText}': " + e.Message); }
        }

        /// <summary>
        /// A few settings live in the file twice: once as the button's own value, which is stored
        /// by name and is exact, and once as a copy under misc or sounds. SaveButton only rewrites
        /// the button entry, so without this the copies kept whatever the last full snapshot saw
        /// and were then restored over the top of the value the player had chosen. Three dictionary
        /// writes, so it runs on every button change rather than tracking which ones are affected.
        /// </summary>
        private static void SyncMirroredSettings(PreferencesData data)
        {
            if (data == null) return;

            data.misc ??= new Dictionary<string, object>();
            data.sounds ??= new Dictionary<string, string>();

            data.misc["themeType"] = themeType;
            data.sounds["Button"] = SoundManager.DefaultSounds.TryGetValue("Button", out string btn) ? btn : "Default";
            data.sounds["Notification"] = SoundManager.DefaultSounds.TryGetValue("Notification", out string notif) ? notif : "None";
        }

        public static void SaveCustomTheme(CustomThemeData theme)
        {
            if (IsApplyingPreferences) return;

            try
            {
                _cache ??= BuildFullSnapshot();
                _cache.customTheme = theme;
                FlushNow();
            }
            catch (Exception e) { LogManager.Log("Error saving custom theme: " + e.Message); }
        }

        public static CustomThemeData GetCustomTheme() => _cache?.customTheme;

        public static string ExportToText() =>
            JsonConvert.SerializeObject(BuildFullSnapshot(), Formatting.None);

        public static void ImportFromText(string json)
        {
            try
            {
                var data = JsonConvert.DeserializeObject<PreferencesData>(json);
                Apply(data);
                Save();
            }
            catch (Exception e) { LogManager.Log("Error importing preferences from text: " + e.Message); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(JsonPath))
                {
                    if (File.Exists(LegacyPath))
                    {
                        MigrateLegacy();
                        hasLoadedPreferences = true;
                        return;
                    }

                    hasLoadedPreferences = true;
                    return;
                }

                PreferencesData data = TryRead(JsonPath);

                if (data == null)
                {
                    // The main file is truncated or corrupt. The atomic write keeps the last good
                    // copy alongside it, so a crash no longer costs the player their settings.
                    LogManager.LogError($"Preferences file '{Path.GetFileName(JsonPath)}' is unreadable, falling back to the backup copy.");
                    data = TryRead(JsonPath + ".bak");
                }

                if (data != null)
                    Apply(data);
            }
            catch (Exception e) { LogManager.Log("Error loading preferences: " + e.Message); }

            hasLoadedPreferences = true;
        }

        /// <summary>
        /// Reads one preferences file, returning null instead of throwing so the caller can try the
        /// next candidate.
        /// </summary>
        private static PreferencesData TryRead(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                var data = JsonConvert.DeserializeObject<PreferencesData>(File.ReadAllText(path));

                if (data == null)
                    return null;

                if (data.schemaVersion > CurrentSchemaVersion)
                {
                    LogManager.LogError($"Preferences were written by a newer build of the menu (schema {data.schemaVersion}, this build understands {CurrentSchemaVersion}). They are left untouched rather than being partially applied and overwritten. Delete {Path.GetFileName(path)} to start fresh.");
                    return null;
                }

                if (data.schemaVersion < OldestSupportedSchemaVersion)
                {
                    LogManager.LogError($"Preferences schema {data.schemaVersion} is too old to be read by this build.");
                    return null;
                }

                if (data.schemaVersion < CurrentSchemaVersion)
                {
                    Migrate(data);
                    data.schemaVersion = CurrentSchemaVersion;
                }

                return data;
            }
            catch (Exception e)
            {
                LogManager.LogError($"Could not read '{Path.GetFileName(path)}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Brings an older preferences file up to the current shape. Version 1 had no schema
        /// marker and stored the PlayFab user id in misc under "userId"; that is still read
        /// correctly, so there is nothing to rewrite yet, but the hook exists so the next change
        /// has an obvious place to go.
        /// </summary>
        private static void Migrate(PreferencesData data)
        {
            LogManager.Log($"Migrating preferences from schema {data.schemaVersion} to {CurrentSchemaVersion}.");
        }

        private static void Apply(PreferencesData data)
        {
            if (data == null)
            {
                LogManager.Log("preferences not found!");
                return;
            }

            var stale = new List<string>();
            RunWithoutSaving(() =>
            {
                try { Settings.Panic(); }
                catch (Exception e) { LogManager.Log("error resetting menu: " + e.Message); }

                try
                {
                    favorites.Clear();
                    favorites.AddRange(data.favorites ?? new List<string>());

                    quickActions.Clear();
                    quickActions.AddRange(data.quickActions ?? new List<string>());

                    skipButtons.Clear();
                    skipButtons.AddRange(data.skipButtons ?? new List<string>());
                }
                catch (Exception e) { LogManager.Log("Error restoring favorites/quickActions/skipButtons: " + e.Message); }

                try
                {
                    ModBindings.Clear();
                    foreach (var kv in data.modBindings ?? new Dictionary<string, List<string>>())
                        ModBindings[kv.Key] = kv.Value;
                }
                catch (Exception e) { LogManager.Log("Error restoring mod bindings: " + e.Message); }

                try
                {
                    RestoreSound("Change Button Sound", "Button", data.sounds);
                    RestoreSound("Change Notification Sound", "Notification", data.sounds);
                }
                catch (Exception e) { LogManager.Log("Error restoring sound settings: " + e.Message); }

                try
                {
                    if (!string.IsNullOrEmpty(data.oldId))
                        Important.oldId = data.oldId;
                }
                catch (Exception e) { LogManager.Log("Error restoring oldId: " + e.Message); }

                try
                {
                    if (data.misc != null)
                    {
                        if (data.misc.TryGetValue("pageButtonType", out object pbt)) pageButtonType = SafeInt(pbt, pageButtonType);
                        if (data.misc.TryGetValue("themeType", out object tt)) themeType = SafeInt(tt, themeType);
                        if (data.misc.TryGetValue("fontCycle", out object fc)) fontCycle = SafeInt(fc, fontCycle);
                        if (data.misc.TryGetValue("pageSize", out object ps)) _pageSize = SafeInt(ps, _pageSize);
                        if (data.misc.TryGetValue("playTime", out object pt)) playTime = SafeInt(pt, (int)playTime);

                        if (data.misc.TryGetValue("userId", out object uid) && uid is string uidStr && !string.IsNullOrEmpty(uidStr) && uidStr != "null")
                            Important.oldId = uidStr;
                    }
                }
                catch (Exception e) { LogManager.Log("Error restoring misc settings: " + e.Message); }

                try
                {
                    if (data.customTheme != null)
                        Settings.ApplyTheme(data.customTheme);
                }
                catch (Exception e) { LogManager.Log("Error applying custom theme: " + e.Message); }

                // Buttons are restored last on purpose. Several settings are written to the file
                // twice: once as the button's own value, which is stored by name and is exact, and
                // once as a copy under misc/sounds. Restoring those copies first lets the loop
                // below re-run each setting's apply callback afterwards, so the value that was
                // actually picked always wins. The other order applied a stale copy on top of the
                // good value, which is what left the menu on the old theme and the click sound
                // back on "Wood" after a restart.
                foreach (KeyValuePair<string, SavedButtonState> kv in data.buttons ?? new Dictionary<string, SavedButtonState>())
                {
                    ButtonInfo b = Buttons.GetIndex(kv.Key);
                    if (b == null)
                    {
                        LogManager.LogError($"could not find button '{kv.Key}', so we just gonna remove it from preferences.");
                        stale.Add(kv.Key);
                        continue;
                    }

                    try
                    {
                        if (!string.IsNullOrEmpty(kv.Value.rebindKey))
                            b.rebindKey = kv.Value.rebindKey;

                        if (kv.Value.enabled == true && b.isTogglable && !b.enabled && !b.label)
                            Toggle(b.buttonText);

                        if (b.isSetting && kv.Value.value != null)
                        {
                            b.value = kv.Value.value;
                            b.onValueChanged?.Invoke();
                        }
                    }
                    catch (Exception e)
                    {
                        LogManager.Log($"Failed to restore button '{kv.Key}' (value type: {kv.Value.value?.GetType().Name}, value: {kv.Value.value}): {e.Message}");
                    }
                }

                foreach (var key in stale)
                    data.buttons.Remove(key);

                // disableLocalSoundboard only exists as this one top level field, so it is applied
                // after the button loop to keep owning the button's enabled state.
                try
                {
                    if (data.disableLocalSoundboard.HasValue)
                    {
                        Sound.disableLocalSoundboard = data.disableLocalSoundboard.Value;

                        ButtonInfo disableLocalBtn = Buttons.GetIndex("Disable Local Soundboard");
                        if (disableLocalBtn != null)
                            disableLocalBtn.enabled = Sound.disableLocalSoundboard;
                    }
                }
                catch (Exception e) { LogManager.Log("Error restoring soundboard setting: " + e.Message); }
            });

            try
            {
                var liveSnapshot = BuildFullSnapshot();
                var merged = new PreferencesData
                {
                    buttons = new Dictionary<string, SavedButtonState>(liveSnapshot.buttons)
                };

                if (data.buttons != null)
                {
                    foreach (var kv in data.buttons)
                    {
                        if (Buttons.GetIndex(kv.Key) != null && !merged.buttons.ContainsKey(kv.Key))
                            merged.buttons[kv.Key] = kv.Value;
                    }
                }

                merged.favorites = liveSnapshot.favorites;
                merged.quickActions = liveSnapshot.quickActions;
                merged.skipButtons = liveSnapshot.skipButtons;
                merged.modBindings = liveSnapshot.modBindings;
                merged.sounds = liveSnapshot.sounds;
                merged.disableLocalSoundboard = liveSnapshot.disableLocalSoundboard;
                merged.customTheme = liveSnapshot.customTheme;
                merged.oldId = liveSnapshot.oldId;
                merged.misc = liveSnapshot.misc;

                _cache = merged;
            }
            catch (Exception e)
            {
                LogManager.Log("Error applying preferences: " + e.Message);
            }
        }

        private static int SafeInt(object value, int fallback)
        {
            try { return Convert.ToInt32(value); }
            catch { return fallback; }
        }

        private static void RestoreSound(string buttonName, string soundKey, Dictionary<string, string> sounds)
        {
            if (sounds == null || !sounds.TryGetValue(soundKey, out string saved) || string.IsNullOrEmpty(saved))
                return;

            SoundManager.DefaultSounds[soundKey] = saved;

            ButtonInfo button = Buttons.GetIndex(buttonName);
            if (button != null)
                button.overlapText = $"{buttonName} <color=grey>[</color><color=green>{saved}</color><color=grey>]</color>";
        }

        private static void MigrateLegacy()
        {
            try
            {
                LogManager.Log("Migrating legacy preferences");

                string text = File.ReadAllText(LegacyPath);

                RunWithoutSaving(() => Settings.LoadPreferencesFromText(text));

                _cache = BuildFullSnapshot();
                FlushNow();

                File.Move(LegacyPath, LegacyPath + ".migrated");
            }
            catch (Exception e) { LogManager.Log("Error migrating legacy preferences: " + e.Message); }
        }
    }
}