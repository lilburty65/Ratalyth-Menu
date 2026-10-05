/*
 * Ratalyth Menu  Managers/VoiceManagerCompat.cs
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

using UnityEngine;

namespace Seralyth.Managers
{
    public class VoiceManager
    {
        private static VoiceManager instance;

        public static VoiceManager Instance => instance ??= new VoiceManager();

        public Ratalyth.Managers.VoiceManager.Clip AudioClip(AudioClip audioClip, bool disableMicrophone = false)
            => Ratalyth.Managers.VoiceManager.Get().AudioClip(audioClip, disableMicrophone);

        public bool StopAudioClip(Ratalyth.Managers.VoiceManager.Clip clip)
            => Ratalyth.Managers.VoiceManager.Get().StopAudioClip(clip);
    }
}