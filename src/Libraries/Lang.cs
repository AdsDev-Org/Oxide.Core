﻿extern alias References;

using Oxide.Core.Plugins;
using References::Newtonsoft.Json;
using References::ProtoBuf;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Oxide.Core.Libraries
{
    public class Lang : Library
    {
        #region Initialization

        public override bool IsGlobal => false;

        private const string defaultLang = "en";
        private readonly LangData langData;
        private readonly Dictionary<string, Dictionary<string, string>> langFiles;
        private readonly Dictionary<Plugin, Event.Callback<Plugin, PluginManager>> pluginRemovedFromManager;

        [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
        private class LangData
        {
            public string Lang = defaultLang;
            public readonly Dictionary<string, string> UserData = new Dictionary<string, string>();
        }

        /// <summary>
        /// Initializes a new instance of the Lang class
        /// </summary>
        public Lang()
        {
            langFiles = new Dictionary<string, Dictionary<string, string>>();
            langData = ProtoStorage.Load<LangData>("oxide.lang") ?? new LangData();
            pluginRemovedFromManager = new Dictionary<Plugin, Event.Callback<Plugin, PluginManager>>();
        }

        #endregion Initialization

        #region Library Functions

        /// <summary>
        /// Registers a language set for a plugin
        /// </summary>
        /// <param name="messages"></param>
        /// <param name="plugin"></param>
        /// <param name="lang"></param>
        [LibraryFunction("RegisterMessages")]
        public void RegisterMessages(Dictionary<string, string> messages, Plugin plugin, string lang = defaultLang)
        {
            if (messages == null || string.IsNullOrEmpty(lang) || plugin == null)
            {
                return;
            }

            string file = $"{lang}{Path.DirectorySeparatorChar}{plugin.Name}.json";
            Dictionary<string, string> existingMessages = GetMessageFile(plugin.Name, lang);

            bool changed;
            if (existingMessages == null)
            {
                langFiles.Remove(file);
                AddLangFile(file, messages, plugin);
                changed = true;
            }
            else
            {
                changed = MergeMessages(existingMessages, messages);
                messages = existingMessages;
            }

            if (!changed)
            {
                return;
            }

            if (!Directory.Exists(Path.Combine(Interface.Oxide.LangDirectory, lang)))
            {
                Directory.CreateDirectory(Path.Combine(Interface.Oxide.LangDirectory, lang));
            }

            File.WriteAllText(Path.Combine(Interface.Oxide.LangDirectory, file), JsonConvert.SerializeObject(messages, Formatting.Indented));
        }

        /// <summary>
        /// Gets the language for the player, fall back to the default server language if no language is set
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [LibraryFunction("GetLanguage")]
        public string GetLanguage(string userId)
        {
            if (!string.IsNullOrEmpty(userId) && langData.UserData.TryGetValue(userId, out string lang))
            {
                return lang;
            }

            return langData.Lang;
        }

        /// <summary>
        /// Gets all available languages or only those for a single plugin
        /// </summary>
        /// <param name="plugin"></param>
        /// <returns></returns>
        [LibraryFunction("GetLanguages")]
        public string[] GetLanguages(Plugin plugin = null)
        {
            List<string> languages = new List<string>();
            foreach (string directory in Directory.GetDirectories(Interface.Oxide.LangDirectory))
            {
                if (Directory.GetFiles(directory).Length == 0)
                {
                    continue;
                }

                if (plugin == null || plugin != null && File.Exists(Path.Combine(directory, $"{plugin.Name}.json")))
                {
                    languages.Add(directory.Substring(Interface.Oxide.LangDirectory.Length + 1));
                }
            }
            return languages.ToArray();
        }

        /// <summary>
        /// Gets a message for a plugin in the players set language
        /// </summary>
        /// <param name="key"></param>
        /// <param name="plugin"></param>
        /// <param name="userId"></param>
        /// <returns></returns>
        [LibraryFunction("GetMessage")]
        public string GetMessage(string key, Plugin plugin, string userId = null)
        {
            if (string.IsNullOrEmpty(key) || plugin == null)
            {
                return key;
            }

            return GetMessageKey(key, plugin, GetLanguage(userId));
        }

        /// <summary>
        /// Gets a message for a plugin in the specified language
        /// </summary>
        /// <param name="key"></param>
        /// <param name="plugin"></param>
        /// <param name="lang"></param>
        /// <returns></returns>
        [LibraryFunction("GetMessageByLanguage")]
        public string GetMessageByLanguage(string key, Plugin plugin, string lang = defaultLang)
        {
            if (string.IsNullOrEmpty(key) || plugin == null)
            {
                return key;
            }

            return GetMessageKey(key, plugin, lang);
        }

        /// <summary>
        /// Gets all messages for a plugin in a language
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetMessages")]
        public Dictionary<string, string> GetMessages(string lang, Plugin plugin)
        {
            if (string.IsNullOrEmpty(lang) || plugin == null)
            {
                return null;
            }

            string file = $"{lang}{Path.DirectorySeparatorChar}{plugin.Name}.json";

            if (!langFiles.TryGetValue(file, out Dictionary<string, string> langFile))
            {
                langFile = GetMessageFile(plugin.Name, lang);
                if (langFile == null)
                {
                    return null;
                }

                AddLangFile(file, langFile, plugin);
            }
            return langFile.ToDictionary(k => k.Key, v => v.Value);
        }

        /// <summary>
        /// Gets the default language for the server
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetServerLanguage")]
        public string GetServerLanguage() => langData.Lang;

        /// <summary>
        /// Sets the language preference for the player
        /// </summary>
        /// <param name="lang"></param>
        /// <param name="userId"></param>
        [LibraryFunction("SetLanguage")]
        public void SetLanguage(string lang, string userId)
        {
            if (string.IsNullOrEmpty(lang) || string.IsNullOrEmpty(userId))
            {
                return;
            }

            if (langData.UserData.TryGetValue(userId, out string currentLang) && lang.Equals(currentLang))
            {
                return;
            }

            langData.UserData[userId] = lang;
            SaveData();
        }

        /// <summary>
        /// Sets the default language for the server
        /// </summary>
        /// <param name="lang"></param>
        [LibraryFunction("SetServerLanguage")]
        public void SetServerLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang) || lang.Equals(langData.Lang))
            {
                return;
            }

            langData.Lang = lang;
            SaveData();
        }

        #endregion Library Functions

        #region Lang Handling

        /// <summary>
        /// Caches a filename and attaches the plugin remove callback
        /// </summary>
        /// <param name="file"></param>
        /// <param name="langFile"></param>
        /// <param name="plugin"></param>
        private void AddLangFile(string file, Dictionary<string, string> langFile, Plugin plugin)
        {
            langFiles.Add(file, langFile);
            if (plugin != null && !pluginRemovedFromManager.ContainsKey(plugin))
            {
                pluginRemovedFromManager[plugin] = plugin.OnRemovedFromManager.Add(plugin_OnRemovedFromManager);
            }
        }

        /// <summary>
        /// Loads a specific language file for a plugin
        /// </summary>
        /// <param name="plugin"></param>
        /// <param name="lang"></param>
        /// <returns></returns>
        private Dictionary<string, string> GetMessageFile(string plugin, string lang = defaultLang)
        {
            if (string.IsNullOrEmpty(plugin))
            {
                return null;
            }

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                lang = lang.Replace(invalidChar, '_');
            }

            string file = $"{lang}{Path.DirectorySeparatorChar}{plugin}.json";
            string filename = Path.Combine(Interface.Oxide.LangDirectory, file);
            return File.Exists(filename) ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(filename)) : null;
        }

        /// <summary>
        /// Loads a specific key from the requested language file for a plugin
        /// </summary>
        /// <param name="key"></param>
        /// <param name="plugin"></param>
        /// <param name="lang"></param>
        /// <returns></returns>
        private string GetMessageKey(string key, Plugin plugin, string lang = defaultLang)
        {
            string file = $"{lang}{Path.DirectorySeparatorChar}{plugin.Name}.json";

            if (!langFiles.TryGetValue(file, out Dictionary<string, string> langFile))
            {
                langFile = GetMessageFile(plugin.Name, lang) ?? (GetMessageFile(plugin.Name, langData.Lang) ?? GetMessageFile(plugin.Name));
                if (langFile == null)
                {
                    Interface.Oxide.LogWarning($"Plugin '{plugin.Name}' is using the Lang API but has no messages registered");
                    return key;
                }

                Dictionary<string, string> defaultLangFile = GetMessageFile(plugin.Name);
                if (defaultLangFile != null && MergeMessages(langFile, defaultLangFile) && File.Exists(Path.Combine(Interface.Oxide.LangDirectory, file)))
                {
                    File.WriteAllText(Path.Combine(Interface.Oxide.LangDirectory, file), JsonConvert.SerializeObject(langFile, Formatting.Indented));
                }

                AddLangFile(file, langFile, plugin);
            }

            return langFile.TryGetValue(key, out string message) ? message : key;
        }

        /// <summary>
        /// Update an existing language file by adding new keys and removing old keys
        /// </summary>
        /// <param name="existingMessages"></param>
        /// <param name="messages"></param>
        /// <returns></returns>
        private bool MergeMessages(Dictionary<string, string> existingMessages, Dictionary<string, string> messages)
        {
            bool changed = false;

            foreach (KeyValuePair<string, string> message in messages)
            {
                if (existingMessages.ContainsKey(message.Key))
                {
                    continue;
                }

                existingMessages.Add(message.Key, message.Value);
                changed = true;
            }

            if (existingMessages.Count > 0)
            {
                foreach (string message in existingMessages.Keys.ToArray())
                {
                    if (messages.ContainsKey(message))
                    {
                        continue;
                    }

                    existingMessages.Remove(message);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Saves all data to the "oxide.lang" file
        /// </summary>
        private void SaveData() => ProtoStorage.Save(langData, "oxide.lang");

        /// <summary>
        /// Called when the plugin was unloaded
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="manager"></param>
        private void plugin_OnRemovedFromManager(Plugin sender, PluginManager manager)
        {
            if (pluginRemovedFromManager.TryGetValue(sender, out Event.Callback<Plugin, PluginManager> callback))
            {
                callback.Remove();
                pluginRemovedFromManager.Remove(sender);
            }

            string[] langs = GetLanguages(sender);
            foreach (string lang in langs)
            {
                langFiles.Remove($"{lang}{Path.DirectorySeparatorChar}{sender.Name}.json");
            }
        }

        #endregion Lang Handling
    }
}
