/*
 * Ratalyth Menu  Plugin.MelonLoader.cs
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


using MelonLoader;
using Ratalyth.Managers;

[assembly: MelonInfo(typeof(Ratalyth.PluginMelonLoader), Ratalyth.PluginInfo.Name, Ratalyth.PluginInfo.Version, "Ratalyth")]
[assembly: MelonOptionalDependencies("BepInEx")]
namespace Ratalyth
{
    public class PluginMelonLoader : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LogManager.SetLogger((level, msg) =>
            {
                switch (level)
                {
                    case Level.Error:
                        LoggerInstance.Error(msg);
                        break;
                    case Level.Warning:
                        LoggerInstance.Warning(msg);
                        break;
                    default:
                        LoggerInstance.Msg(msg);
                        break;
                }
            });

            Bootstrapper.Initialize();
        }

        public override void OnDeinitializeMelon()
        {
            // UnloadMenu switches every enabled mod back off, so the live button states stop
            // matching what the player chose right after this runs. Saving first is the only thing
            // that captures the final state during a normal shutdown. Without it, anything changed
            // since the last write was lost unless the player happened to quit through the menu's
            // own "Exit Gorilla Tag" button, which is the only other place Save() was called from.
            try { Classes.Menu.Preferences.Save(); }
            catch { }

            Menu.Main.UnloadMenu();
        }
    }
}
