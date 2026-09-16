using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ServerSync;

namespace BuildResourcesModNamespace
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class BuildResourcesMod : BaseUnityPlugin
    {
        public const string PluginGuid = "Jammerbam.buildresourcesmod";
        public const string PluginName = "Build Resources Mod";
        public const string PluginVersion = "1.2.1";

        private static BuildResourcesMod Instance;
        private static ConfigSync configSync = new ConfigSync("BuildResourcesMod")
        {
            DisplayName = PluginName,
            CurrentVersion = PluginVersion,
            MinimumRequiredVersion = PluginVersion,
            IsLocked = true,
            ModRequired = true
        };

        private Dictionary<GameObject, string> PieceToTableMap = new Dictionary<GameObject, string>();
        private Dictionary<string, PieceTable> CachedPieceTables = new Dictionary<string, PieceTable>();
        private Dictionary<Piece, Dictionary<string, int>> OriginalResourceCache = new Dictionary<Piece, Dictionary<string, int>>();
        private ConfigEntry<bool> EnableLogging;
        private Dictionary<string, SyncedConfigEntry<bool>> CategoryConfigs = new Dictionary<string, SyncedConfigEntry<bool>>();
        private SyncedConfigEntry<string> Exceptions;
        private SyncedConfigEntry<bool> EnableSkillBasedReduction;
        private SyncedConfigEntry<bool> EnableFreeBuildAtMaxSkill;

        private HashSet<string> ExceptionPieces = new HashSet<string>();
        private bool LastChecked;
        private string LastPiece;
        private bool NoCost;


        public static void Dbgl(string message, bool pref = true)
        {
            if (Instance?.EnableLogging?.Value == true)
            {
                Debug.Log((pref ? "[BuildResourcesMod] " : "") + message);
            }
        }

        public static void Dbglw(string message, bool pref = true)
        {
            if (Instance?.EnableLogging?.Value == true)
            {
                Debug.LogWarning((pref ? "[BuildResourcesMod] " : "") + message);
            }
        }
        

        void Awake()
        {
            Instance = this;
        
            // Initialize core configurations
            EnableLogging = Config.Bind("Debug", "EnableLogging", false, "Enable detailed logging for debugging.");
            Dbgl($"Initialized EnableLogging: {EnableLogging.Value}");
            EnableSkillBasedReduction = configSync.AddConfigEntry(Config.Bind("Features", "EnableSkillBasedReduction", false, "Enable resource cost reduction based on crafting skill."));
            EnableFreeBuildAtMaxSkill = configSync.AddConfigEntry(Config.Bind("Features", "EnableFreeBuildAtMaxSkill", true, "Allow free building when the player has maximum crafting skill."));

            AddCategoryConfig("Misc", "Require resources for Misc category.", true);
            AddCategoryConfig("Crafting", "Require resources for Crafting category.", true);
            AddCategoryConfig("Furniture", "Require resources for Furniture category.", false);
            AddCategoryConfig("BuildingWorkbench", "Require resources for BuildingWorkbench category.", false);
            AddCategoryConfig("BuildingStonecutter", "Require resources for BuildingStonecutter category.", false);
            // Added in Valheim 1.0. Despite the name, this category holds the new 67-degree steep roof/wall/beam
            // building pieces (wood_roof_67, wood_wall_roof_67_a, ...), so it defaults to free like the other
            // building tabs.
            AddCategoryConfig("DeepNorth", "Require resources for DeepNorth category (Valheim 1.0+; holds the 67-degree steep roof and wall pieces).", false);
            AddCategoryConfig("Cultivator", "Require resources for the cultivator.", true);
            AddCategoryConfig("Hoe", "Require resources for the hoe.", true);
        
            Exceptions = configSync.AddConfigEntry(
            Config.Bind("Exceptions", "PieceExceptions", "", "Comma-separated list of pieces that are always buildable.")
            );

            var harmony = new Harmony("Jammerbam.buildresourcesmod");
            harmony.PatchAll();
        
            Dbgl("Mod initialized successfully.");
        }

        
        void Start()
        {
            StartCoroutine(WaitForGameLoad());
            StartCoroutine(WaitForConfigSync());
        }

        private System.Collections.IEnumerator WaitForGameLoad()
        {
            Dbgl("Waiting for the game to fully initialize...");

            // Initial delay to allow all mods and game assets to initialize
            yield return new WaitForSeconds(5f);

            // Ensure that primary objects for mod categories are loaded
            while (Resources.FindObjectsOfTypeAll<PieceTable>().Length == 0)
            {
                yield return null; // Wait until piece tables are available
            }

            Dbgl("Game initialization complete. Proceeding with category detection.");
            HandleCategories();

            // Wait for the world to fully load
            yield return new WaitUntil(() => ZNetScene.instance != null);

            Dbgl("World initialization complete. Performing post-load routines.");
            HandleCategories();
            CachePieceTables();

            yield return new WaitUntil(() => Player.m_localPlayer != null);

            Dbgl("Local Player has loaded, applying resource reduction");
            BuildResourcesMod.Instance.StartCoroutine(BuildResourcesMod.Instance.ApplyResourceReductions());
        }

        private System.Collections.IEnumerator WaitForConfigSync()
        {
            Dbgl("Waiting for config sync to complete...");

            // Wait until the config sync is complete (InitialSyncDone is public in current ServerSync)
            yield return new WaitUntil(() => configSync.InitialSyncDone);
            ParseExceptions();
        }



        private void AddCategoryConfig(string category, string description, bool defaultValue)
        {
            CategoryConfigs[category] = configSync.AddConfigEntry(
                Config.Bind("Categories", $"{category}RequiresResources", defaultValue, description)
            );
        }
  

        private void ParseExceptions()
        {
            Dbgl("Parsing exceptions...");
            ExceptionPieces.Clear();
            if (string.IsNullOrWhiteSpace(Exceptions.Value))
            {
                Dbgl("No exception pieces specified in config.");
            }

            var pieces = Exceptions.Value.Split(',');
            foreach (string piece in pieces)
            {
                string trimmedPiece = piece.Trim();
                if (!string.IsNullOrEmpty(trimmedPiece))
                {
                    ExceptionPieces.Add(trimmedPiece);
                    Dbgl($"Exception added: {trimmedPiece}");
                }
            }
            Dbgl($"Final list of exception pieces: {string.Join(", ", ExceptionPieces)}");
        } 


        private void CachePieceTables()
        {
            CachedPieceTables.Clear();
            PieceToTableMap.Clear();
            OriginalResourceCache.Clear();

            foreach (var pieceTable in Resources.FindObjectsOfTypeAll<PieceTable>())
            {
                if (!CachedPieceTables.ContainsKey(pieceTable.name))
                {
                    CachedPieceTables[pieceTable.name] = pieceTable;
                    Dbgl($"Cached PieceTable: {pieceTable.name}");

                    foreach (var pieceObject in pieceTable.m_pieces)
                    {
                        if (pieceObject != null && !PieceToTableMap.ContainsKey(pieceObject))
                        {
                            Piece piece = pieceObject.GetComponent<Piece>();
                            if (piece != null)
                            {
                                PieceToTableMap[pieceObject] = pieceTable.name;
                                Dbgl($" - Piece: {pieceObject.name} belongs to PieceTable: {pieceTable.name}");

                                // Cache the original resource amounts
                                var resourceAmounts = new Dictionary<string, int>();
                                foreach (var req in piece.m_resources)
                                {
                                    if (req.m_resItem != null)
                                    {
                                        resourceAmounts[req.m_resItem.name] = req.m_amount;
                                    }
                                }

                                OriginalResourceCache[piece] = resourceAmounts;
                                Dbgl($"Cached original resources for piece: {piece.name}");
                            }
                        }
                    }
                }
            }

            if (CachedPieceTables.Count == 0)
            {
                Dbglw("No piece tables were found to cache.");
            }
        }


        private void HandleCategories()
        {
            Dbgl("Scanning game for available categories...");

            HashSet<string> blacklist = new HashSet<string>
            {
                "Misc",
                "Crafting",
                "Furniture",
                "BuildingWorkbench",
                "BuildingStonecutter",
                "DeepNorth",
                "All",
                "Max",
                "Meads",
                "Feasts",
                "Food"
            };

            HashSet<string> processedCategories = new HashSet<string>();
            HashSet<string> detectedCategories = new HashSet<string>();

            foreach (var pieceTable in Resources.FindObjectsOfTypeAll<PieceTable>())
            {
                foreach (var obj in pieceTable.m_pieces)
                {
                    if (obj != null)
                    {
                        Piece piece = obj.GetComponent<Piece>();
                        if (piece == null) continue;

                        string category = piece.m_category.ToString();

                        if (processedCategories.Contains(category))
                        {
                            continue; // Skip already processed categories
                        }

                        processedCategories.Add(category);

                        if (blacklist.Contains(category))
                        {
                            Dbgl($"Skipping blacklisted category: {category}");
                            continue;
                        }

                        detectedCategories.Add(category);
                        Dbgl($"Detected modded category: {category}");

                        // Dynamically create or update configuration for this category
                        if (!CategoryConfigs.ContainsKey(category))
                        {
                            // Valheim 1.0 piece tables carry a label per category; use it for the description
                            // so modded (numeric) categories are identifiable in the config file.
                            string localizedCategory = category;
                            int labelIndex = pieceTable.m_categories != null ? pieceTable.m_categories.IndexOf(piece.m_category) : -1;
                            if (labelIndex >= 0 && pieceTable.m_categoryLabels != null && labelIndex < pieceTable.m_categoryLabels.Count
                                && !string.IsNullOrEmpty(pieceTable.m_categoryLabels[labelIndex]))
                            {
                                localizedCategory = Localization.instance != null
                                    ? Localization.instance.Localize(pieceTable.m_categoryLabels[labelIndex])
                                    : pieceTable.m_categoryLabels[labelIndex];
                            }

                            Dbgl($"Adding new config entry for category: {localizedCategory}");
                            
                            CategoryConfigs[category] = configSync.AddConfigEntry(
                                Config.Bind(
                                    "Categories",
                                    $"{category}RequiresResources",
                                    true,
                                    new ConfigDescription($"Require resources for modded category: {localizedCategory}")
                                )
                            );
                        }
                        else
                        {
                            Dbgl($"Category '{category}' already exists in the configuration.");
                        }
                    }
                }
            }

            if (detectedCategories.Count == 0)
            {
                Dbgl("No additional modded categories detected.");
            }
            else
            {
                Dbgl($"Detected and added {detectedCategories.Count} new modded categories.");
            }
        }


        public System.Collections.IEnumerator ApplyResourceReductions()
        {
            if (OriginalResourceCache == null || Player.m_localPlayer == null) yield break;

            // Hardcoded list of non-reducible pieces
            HashSet<string> NonReduciblePieces = new HashSet<string>
            {
                "stone_pile",
                "coal_pile",
                "blackmarble_pile",
                "grausten_pile",
                "skull_pile",
                "treasure_pile",
                "wood_stack",
                "wood_fine_stack",
                "wood_core_stack",
                "wood_yggdrasil_stack",
                "blackwood_stack",
                "bone_stack",
                "treasure_stack"
            };

            if (EnableSkillBasedReduction.Value)
            {
                float skillFactor = Player.m_localPlayer.GetSkillFactor(Skills.SkillType.Crafting);
                Dbgl($"Applying resource reductions with coroutine. Current crafting skill factor: {skillFactor}");

                int processedCount = 0;

                foreach (var entry in OriginalResourceCache)
                {
                    Piece piece = entry.Key;
                    var originalAmounts = entry.Value;

                    // Check if the piece is in the non-reducible list
                    string rawName = piece.name.Replace("(Clone)", "").Trim().ToLowerInvariant();
                    if (NonReduciblePieces.Contains(rawName))
                    {
                        Dbgl($"Skipping resource reduction for non-reducible piece: {piece.name}");
                        continue;
                    }

                    foreach (var req in piece.m_resources)
                    {
                        if (req.m_resItem == null) continue;

                        if (originalAmounts.TryGetValue(req.m_resItem.name, out int originalAmount))
                        {
                            // Reduce resource cost based on skill level
                            int reducedAmount = Mathf.CeilToInt(originalAmount * (1.0f - skillFactor));
                            if (!(req.m_amount == reducedAmount || req.m_amount == 1))
                            {
                                // Enforce a minimum value of 1 to prevent sprite disappearance
                                req.m_amount = Mathf.Max(reducedAmount, 1);

                                if (EnableFreeBuildAtMaxSkill.Value && Mathf.Approximately(skillFactor, 1.0f))
                                {
                                    NoCost = true; // Free building for max skill
                                    req.m_amount = 1; // Keep the sprite visible by setting to at least 1
                                }
                                else
                                {
                                    NoCost = false;
                                }

                                Dbgl($"Updated resource {req.m_resItem.name} for piece {piece.name} from: {originalAmount} to: {req.m_amount}");
                            }
                        }
                    }

                    processedCount++;

                    // Yield every 5 pieces to allow frame updates
                    if (processedCount % 5 == 0)
                    {
                        yield return null;
                    }
                }
                Dbgl("Resource reductions applied successfully.");
            }
            else
            {
                Dbgl("Resource reduction config set to false.");
            }
        }



        private bool ShouldRequireResources(Piece piece)
        {
            if (piece == null)
            {
                Dbglw("Piece is null in ShouldRequireResources check.");
                return true; // Default to requiring resources if piece is null
            }

            if (LastPiece == piece.name)
            {
                return LastChecked;
            }

            string rawName = piece.name.Replace("(Clone)", "").Trim().ToLowerInvariant();

            // Check exceptions by raw internal name
            if (ExceptionPieces.Contains(rawName))
            {
                Dbgl($"Piece '{piece.name}' is in the exception list. Skipping resource requirement.");
                LastPiece = piece.name;
                LastChecked = false;
                return false;
            }

            // Check if the piece belongs to the cultivator
            if (PieceToTableMap.TryGetValue(piece.gameObject, out string tableName))
            {
                if (tableName == "_CultivatorPieceTable")
                {
                    if (CategoryConfigs.TryGetValue("Cultivator", out SyncedConfigEntry<bool> cultivatorConfig))
                    {
                        Dbgl($"Piece '{piece.name}' belongs to the cultivator. Requires resources: {cultivatorConfig.Value}");
                        LastPiece = piece.name;
                        LastChecked = cultivatorConfig.Value;
                        return cultivatorConfig.Value;
                    }
                }
                // Check if the piece belongs to the hoe
                else if (tableName == "_HoePieceTable")
                {
                    if (CategoryConfigs.TryGetValue("Hoe", out SyncedConfigEntry<bool> hoeConfig))
                    {
                        Dbgl($"Piece '{piece.name}' belongs to the hoe. Requires resources: {hoeConfig.Value}");
                        LastPiece = piece.name;
                        LastChecked = hoeConfig.Value;
                        return hoeConfig.Value;
                    }
                }
            }

            // Check if the piece belongs to a valid category
            string category = piece.m_category.ToString();
            if (CategoryConfigs.TryGetValue(category, out SyncedConfigEntry<bool> config))
            {
                Dbgl($"Piece '{piece.name}' in category '{category}' requires resources: {config.Value}");
                LastPiece = piece.name;
                LastChecked = config.Value;
                return config.Value;
            }

            Dbglw($"Piece '{piece.name}' in category '{category}' has no specific configuration. Defaulting to require resources.");
            LastPiece = piece.name;
            LastChecked = true;
            return true;
        }


        [HarmonyPatch(typeof(Player), "ConsumeResources")]
        [HarmonyPatch(new[] { typeof(Piece.Requirement[]), typeof(int), typeof(int), typeof(int) })]
        public static class ConsumeResourcesPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(Piece.Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier, Player __instance)
            {
                if (BuildResourcesMod.Instance == null) return true;

                // Attempt to locate the currently selected piece from the requirements or context
                foreach (var requirement in requirements)
                {
                    if (requirement.m_resItem == null) continue;

                    // Check the Piece associated with these requirements
                    Piece currentPiece = __instance.GetSelectedPiece(); 
                    if (currentPiece != null && !BuildResourcesMod.Instance.ShouldRequireResources(currentPiece))
                    {
                        Dbgl($"Skipping resource consumption for piece '{currentPiece.m_name}(Token Name)' in category '{currentPiece.m_category}' (mod setting).");
                        return false; // Skip the default resource consumption logic
                    }
                    if (BuildResourcesMod.Instance.NoCost)
                    {
                        return false; //Skip consumption if player has maxed crafting skill
                    }
                }

                // Default behavior if no bypass conditions are met
                return true;
            }
        }


        [HarmonyPatch(typeof(Player), "HaveRequirements")]
        [HarmonyPatch(new[] { typeof(Piece), typeof(Player.RequirementMode) })]
        public static class HaveRequirementsPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(Piece piece, Player.RequirementMode mode, ref bool __result, Player __instance)
            {
                if (BuildResourcesMod.Instance == null) return true;

                // Reflectively access m_knownStations as a Dictionary<string, int>
                var knownStationsField = AccessTools.Field(typeof(Player), "m_knownStations");
                var knownStations = (Dictionary<string, int>)knownStationsField.GetValue(__instance);

                // Crafting station check
                if (piece.m_craftingStation)
                {
                    if (mode == Player.RequirementMode.IsKnown || mode == Player.RequirementMode.CanAlmostBuild)
                    {
                        if (!knownStations.ContainsKey(piece.m_craftingStation.m_name))
                        {
                            __result = false;
                            return false; // Block placement if the station is unknown
                        }
                    }
                    else if (!CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, __instance.transform.position)
                            && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
                    {
                        __result = false;
                        return false; // Block placement if the station is out of range
                    }
                }

                // DLC check
                if (piece.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(piece.m_dlc))
                {
                    __result = false;
                    return false; // Block placement if the required DLC is not installed
                }

                // Check if the piece requires resources
                if  (mode != Player.RequirementMode.IsKnown)
                {
                    if (!BuildResourcesMod.Instance.ShouldRequireResources(piece) || BuildResourcesMod.Instance.NoCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
                    {
                        __result = true;
                        return false; // Simulate having the requirements
                    }
                }


                // Fall back to the original method's logic for resource checks
                return true;
            }
        }


        [HarmonyPatch(typeof(Piece), "DropResources")]
        public static class DropResourcesPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(Piece __instance)
            {
                if (BuildResourcesMod.Instance == null) return true;

                // Use ShouldRequireResources to determine if resources should be dropped
                if (!BuildResourcesMod.Instance.ShouldRequireResources(__instance))
                {
                    Dbgl($"Skipping resource drop for piece '{__instance.name}' (Mod Setting).");
                    return false; // Prevent resource drop
                }

                // Default behavior (resources are dropped)
                Dbgl($"Dropping resources for piece '{__instance.name}'.");
                return true;
            }
        }


        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        public static class SetupPieceInfoPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Piece piece, Hud __instance)
            {
                if (piece == null || BuildResourcesMod.Instance == null) return;

                // Iterate over all resource slots in the UI
                for (int j = 0; j < piece.m_resources.Length; j++)
                {
                    Piece.Requirement req = piece.m_resources[j];
                    if (req.m_resItem == null) continue;

                        // Find the resource amount text element
                        GameObject resourceSlot = __instance.m_requirementItems[j];
                        TMP_Text amountText = resourceSlot.transform.Find("res_amount")?.GetComponent<TMP_Text>();
                        Image icon = resourceSlot.transform.Find("res_icon")?.GetComponent<Image>();

                    // Check if resources are not required
                    if (BuildResourcesMod.Instance.NoCost)
                    {
                        if (amountText != null && icon != null)
                        {
                            amountText.color = Color.green; // Display resource number in green
                            amountText.text = "0"; // Ensure the text explicitly shows 0
                            icon.enabled = true;
                        }
                    }
                    else if (!BuildResourcesMod.Instance.ShouldRequireResources(piece))
                    {
                        if (amountText != null && icon != null)
                        {
                            amountText.color = Color.green; //Display resource number in white
                            icon.enabled = true;
                        }
                    }
                }
            }
        }



        //Crafting skill and resource reduction handling


        [HarmonyPatch(typeof(Player), nameof(Player.OnSkillLevelup))]
        public static class SkillChangePatch
        {
            [HarmonyPostfix]
            public static void Postfix(Skills.SkillType skill, float level)
            {
                if (skill != Skills.SkillType.Crafting || BuildResourcesMod.Instance == null) return;

                Dbgl($"Crafting skill updated. Recalculating resources for all cached pieces.");
                BuildResourcesMod.Instance.StartCoroutine(BuildResourcesMod.Instance.ApplyResourceReductions());
            }
        }

        [HarmonyPatch(typeof(Skills), "LowerAllSkills")]
        public static class SkillReductionPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Skills __instance, float factor)
            {
                if (BuildResourcesMod.Instance == null) return;

                BuildResourcesMod.Dbgl($"All skills lowered by {factor * 100}%. Recalculating resources for all cached pieces.");
                BuildResourcesMod.Instance.StartCoroutine(BuildResourcesMod.Instance.ApplyResourceReductions());
            }
        }


        [HarmonyPatch(typeof(Skills), "CheatRaiseSkill")]
        public static class CheatRaiseSkillPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Skills __instance, string name, float value, bool showMessage)
            {
                // Only trigger for the Crafting skill or "all"
                if (name.ToLower() == "all" || name.ToLower() == Skills.SkillType.Crafting.ToString().ToLower())
                {
                    BuildResourcesMod.Dbgl($"Console command used to raise skill. Recalculating resources for all cached pieces.");
                    BuildResourcesMod.Instance.StartCoroutine(BuildResourcesMod.Instance.ApplyResourceReductions());
                }
            }
        }

        [HarmonyPatch(typeof(Skills), "CheatResetSkill")]
        public static class CheatResetSkillPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Skills __instance, string name)
            {
                if (BuildResourcesMod.Instance == null) return;

                // Handle "all" or "Crafting" skill resets
                if (name.ToLower() == "all" || name.ToLower() == Skills.SkillType.Crafting.ToString().ToLower())
                {
                    BuildResourcesMod.Dbgl($"Crafting skill reset via console command. Recalculating resources for all cached pieces.");
                    BuildResourcesMod.Instance.StartCoroutine(BuildResourcesMod.Instance.ApplyResourceReductions());
                }
            }
        }
    }
}
