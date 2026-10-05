/*
 * Ratalyth Menu  PluginInfo.cs
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

using System.Reflection;

namespace Ratalyth
{
    public class PluginInfo
    {
public const string GUID = "org.ratalyth.gorillatag.ratalythmenu";
        public const string Name = "Ratalyth Menu";
        public const string Description = "Community powered mod menu for Gorilla Tag.";
        public const string Version = "1.0.0";

        /// <summary>
        /// Stamped by the build through AssemblyMetadata, read reflectively so building the menu no
        /// longer rewrites this source file. It used to be a const that Directory.Build.targets
        /// edited on disk, which meant every build dirtied a tracked file and the value in git was
        /// whatever the last person happened to build.
        /// </summary>
        public static readonly string BuildTimestamp = ReadBuildTimestamp();

        private static string ReadBuildTimestamp()
        {
            // Reading assembly metadata attributes makes the runtime resolve every attribute applied
            // to this assembly, not just the metadata ones. This build also carries the MelonLoader
            // entry point, so MelonInfoAttribute gets pulled in as well, and under BepInEx there is
            // no MelonLoader.dll to resolve. That threw FileNotFoundException straight out of this
            // method, which is the PluginInfo type initializer, and a type initializer that throws
            // is cached by the runtime and rethrown on every later access to any member. One call
            // took down BaseDirectory, Version, Online and ApiBase for the entire session: the ban
            // list poller never started, and the admin lists were never fetched.
            try
            {
                foreach (var attribute in typeof(PluginInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
                {
                    if (attribute.Key == "BuildTimestamp")
                        return attribute.Value;
                }
            }
            catch (System.Exception e)
            {
                // Reported rather than thrown. LogManager does not read PluginInfo, so this cannot
                // recurse back into the initializer that just failed.
                Ratalyth.Managers.LogManager.LogError(
                    "Could not read the build timestamp from assembly metadata, continuing without it: " + e.Message);
            }

            return "unknown";
        }


        public const string BaseDirectory =
#if LEGAL || LEGAL_DEBUG
            "RatalythMenu/Legal";
#else
            "RatalythMenu";
#endif
        public const string ClientResourcePath = "RatalythMenu.Resources.Client";
public const string ServerResourcePath = "https://raw.githubusercontent.com/Seralyth/Seralyth-Menu/master/Resources/Server";

        /// <summary>
        /// The one place every online feature points at. Swap this single value to move the
        /// API, or set it to "" to run the menu completely offline.
        /// </summary>
        public const string ApiBase = "https://ratalyth-api.lilburty65.workers.dev";

        public static bool Online => !string.IsNullOrEmpty(ApiBase);

        public const string ServerAPI = ApiBase;
        public const string Logo = @"
                                            %%%%%                                                   
                                           %%% %%%%                                                 
                                         %%%      %%%%                                              
                                        %%%         %%%%        %%%  %                              
                                      %%%%            %%%%%%%% %%%%  %%                             
                                     %%%        %#####% %%%%%        %%                             
                                    %%%       ############ %%%                                      
                                  %%%       ######     %###  %%%%     %%%                           
                                %%%%       ######        ###   %#%%    %%                           
                             %%%#%        ######         ###%    %#%%                               
                       %%%%  %%#%         ######         %###      %##% %%                          
                 %%%%  %%   %##           ######%         ##%         %###%                         
                           %#%             ######        ###            ###%                        
                         %##%              %######%    #####              ###%                      
#%   %##                  #######%                        ###                    
                   %% %##                     %#######%                        ###%                 
###                        %########%                       ###%               
###                            %#######%                       %##%             
                  %##                                %#######%                        ###           
                %##%                                   %#######%                     ###%           
###                   %##########%        #######%                   ###             
##%                  %####%    %####        %######%                ###               
###                  %###%        %##%         %######%              ###                
###                 ###%          %%%           %######%            ##%                 
###              %###                          #######          ####                  
                %###           ####                          #######        %###                    
####         ####                          #######       ###   ##                 
                    %###       ####                         %######       ##%    ##%                
###      ###                         ######      ###                         
                         %###   ####                       ######      ###        %%%               
####  %####                   %######     ###           #%               
                            %%###% ####%              ########      ##%         %%%                 
###%%######%%    %#########%      ###     %%%% %%%%                 
                             %#   %### %###############%         ##%%%%% %%%%                       
                              %%    %##%                       %##  %                               
                                       %##                    %#%                                   
                               %%        %#%%               %%%%                                    
                               %%%         %%#%            %%%                                      
                                      %%%%%  %%%%        %%%%                                       
                                 %%%%           %%%     %%%                                         
                                                  %%%% %%%                                          
                                                    %%%%                                            ";

#if DEBUG || LEGAL_DEBUG
        public static bool BetaBuild = true;
#else
        public static bool BetaBuild = true;
#endif
    }
}
