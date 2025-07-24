using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using REPOLib.Extensions;
using REPOLib.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace REPO_Enemy_Drops
{
    [BepInPlugin("com.imvertro.enemydrops", "REPO Enemy Drops", "1.0.0")]
    [BepInProcess("REPO.exe")]
    public class EnemyDropsMod : BaseUnityPlugin
    {
        #region Variáveis e Configurações

        // Configurações do mod
        private ConfigEntry<bool> modEnabled;
        private ConfigEntry<float> globalDropChance;
        private ConfigEntry<float> minDropDistance;
        private ConfigEntry<float> maxDropDistance;
        private ConfigEntry<string> gameObjectPrefix;

        // Configurações de drops específicos
        private ConfigEntry<bool> enablePlayerEnergy;
        private ConfigEntry<float> playerEnergyDropChance;

        private ConfigEntry<bool> enablePlayerExtraJump;
        private ConfigEntry<float> playerExtraJumpDropChance;

        private ConfigEntry<bool> enablePlayerGrabRange;
        private ConfigEntry<float> playerGrabRangeDropChance;

        private ConfigEntry<bool> enablePlayerGrabStrength;
        private ConfigEntry<float> playerGrabStrengthDropChance;

        private ConfigEntry<bool> enablePlayerHealth;
        private ConfigEntry<float> playerHealthDropChance;

        private ConfigEntry<bool> enablePlayerSprintSpeed;
        private ConfigEntry<float> playerSprintSpeedDropChance;

        private ConfigEntry<bool> enablePlayerTumbleLaunch;
        private ConfigEntry<float> playerTumbleLaunchDropChance;

        private ConfigEntry<bool> enableGunTranq;
        private ConfigEntry<float> gunTranqDropChance;

        // Lista de items e suas chances
        private Dictionary<string, ItemDropInfo> itemDrops = new Dictionary<string, ItemDropInfo>();

        // Armazena prefabs de items já carregados (para cache)
        private Dictionary<string, GameObject> cachedItemPrefabs = new Dictionary<string, GameObject>();

        // Armazena itens do REPOLib.Items
        private Dictionary<string, Item> repoItems = new Dictionary<string, Item>();

        // Dicionário para rastrear inimigos que já droparam itens recentemente
        private static Dictionary<int, float> recentDrops = new Dictionary<int, float>();
        // Tempo em segundos para limpar inimigos do registro (para evitar vazamento de memória)
        private static float dropCooldown = 1.0f;

        // Harmony
        private Harmony harmony;

        // Instância estática para uso nos patches
        public static EnemyDropsMod Instance;

        // Logger
        internal static ManualLogSource Log;

        #endregion

        #region Classes Auxiliares

        private class ItemDropInfo
        {
            public string ItemName { get; set; }
            public bool Enabled { get; set; }
            public float DropChance { get; set; }
            public string PrefabPath { get; set; }
            public List<string> AlternativeNames { get; set; }

            public ItemDropInfo(string itemName, bool enabled, float dropChance, string prefabPath)
            {
                ItemName = itemName;
                Enabled = enabled;
                DropChance = dropChance;
                PrefabPath = prefabPath;
                AlternativeNames = new List<string>();
            }
        }

        #endregion

        #region Inicialização

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Inicializa as configurações
            InitializeConfig();

            // Inicializa a lista de items
            InitializeItems();

            // Aplica os patches usando Harmony com atributos
            harmony = new Harmony("com.imvertro.enemydrops");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.LogInfo($"Plugin REPO Enemy Drops v1.0.0 carregado!");

            // Registra evento para explorar itens quando a cena for carregada
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.LogInfo($"Cena carregada: {scene.name}. Modo: {mode}");

            // Limpa cache de prefabs ao carregar novas cenas
            cachedItemPrefabs.Clear();

            // Limpa o registro de drops ao mudar de cena
            recentDrops.Clear();

            // Executar descoberta em todas as cenas
            DiscoverItemsInGame();

            // Inicializa os itens do jogo usando a referência do REPOLib
            InitializeREPOItems();
        }

        private void InitializeREPOItems()
        {
            try
            {
                Log.LogInfo("Tentando inicializar os itens usando REPOLib...");

                // Aguarda um frame para garantir que tudo esteja inicializado
                StartCoroutine(WaitAndInitItems());
            }
            catch (Exception ex)
            {
                Log.LogError($"Erro ao inicializar itens REPOLib: {ex.Message}");
            }
        }

        private System.Collections.IEnumerator WaitAndInitItems()
        {
            yield return new WaitForSeconds(1f);

            try
            {
                // Tenta obter todos os itens registrados no jogo
                repoItems.Clear();
                if (StatsManager.instance != null)
                {
                    foreach (Item item in StatsManager.instance.GetItems())
                    {
                        string name = item.name.Replace("Item ", string.Empty);
                        if (!repoItems.ContainsKey(name))
                        {
                            repoItems.Add(name, item);
                            Log.LogInfo($"Item encontrado: {name}");
                        }
                    }
                    Log.LogInfo($"Inicializados {repoItems.Count} itens do REPOLib");
                }
                else
                {
                    Log.LogWarning("StatsManager.instance não está disponível ainda");
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Erro ao obter itens do StatsManager: {ex.Message}");
            }
        }

        private void InitializeConfig()
        {
            // Configurações gerais
            modEnabled = Config.Bind("General", "ModEnabled", true, "Ativa ou desativa o mod");
            globalDropChance = Config.Bind("General", "GlobalDropChance", 0.3f, "Chance global de um inimigo soltar um item (0-1)");
            minDropDistance = Config.Bind("General", "MinDropDistance", 0.2f, "Distância mínima do inimigo para o item aparecer");
            maxDropDistance = Config.Bind("General", "MaxDropDistance", 1.0f, "Distância máxima do inimigo para o item aparecer");
            gameObjectPrefix = Config.Bind("General", "ItemGameObjectPrefix", "Item", "Prefixo usado nos GameObjects de itens do jogo");

            // Item Upgrade Player Energy
            enablePlayerEnergy = Config.Bind("Items", "EnablePlayerEnergy", true, "Ativa ou desativa o drop de melhorias de energia");
            playerEnergyDropChance = Config.Bind("Items", "PlayerEnergyDropChance", 0.15f, "Chance de um inimigo soltar melhoria de energia (0-1)");

            // Item Upgrade Player Extra Jump
            enablePlayerExtraJump = Config.Bind("Items", "EnablePlayerExtraJump", true, "Ativa ou desativa o drop de melhorias de pulo extra");
            playerExtraJumpDropChance = Config.Bind("Items", "PlayerExtraJumpDropChance", 0.12f, "Chance de um inimigo soltar melhoria de pulo extra (0-1)");

            // Item Upgrade Player Grab Range
            enablePlayerGrabRange = Config.Bind("Items", "EnablePlayerGrabRange", true, "Ativa ou desativa o drop de melhorias de alcance de agarre");
            playerGrabRangeDropChance = Config.Bind("Items", "PlayerGrabRangeDropChance", 0.12f, "Chance de um inimigo soltar melhoria de alcance de agarre (0-1)");

            // Item Upgrade Player Grab Strength
            enablePlayerGrabStrength = Config.Bind("Items", "EnablePlayerGrabStrength", true, "Ativa ou desativa o drop de melhorias de força de agarre");
            playerGrabStrengthDropChance = Config.Bind("Items", "PlayerGrabStrengthDropChance", 0.12f, "Chance de um inimigo soltar melhoria de força de agarre (0-1)");

            // Item Upgrade Player Health
            enablePlayerHealth = Config.Bind("Items", "EnablePlayerHealth", true, "Ativa ou desativa o drop de melhorias de saúde");
            playerHealthDropChance = Config.Bind("Items", "PlayerHealthDropChance", 0.12f, "Chance de um inimigo soltar melhoria de saúde (0-1)");

            // Item Upgrade Player Sprint Speed
            enablePlayerSprintSpeed = Config.Bind("Items", "EnablePlayerSprintSpeed", true, "Ativa ou desativa o drop de melhorias de velocidade de corrida");
            playerSprintSpeedDropChance = Config.Bind("Items", "PlayerSprintSpeedDropChance", 0.12f, "Chance de um inimigo soltar melhoria de velocidade de corrida (0-1)");

            // Item Upgrade Player Tumble Launch
            enablePlayerTumbleLaunch = Config.Bind("Items", "EnablePlayerTumbleLaunch", true, "Ativa ou desativa o drop de melhorias de impulso de salto");
            playerTumbleLaunchDropChance = Config.Bind("Items", "PlayerTumbleLaunchDropChance", 0.12f, "Chance de um inimigo soltar melhoria de impulso de salto (0-1)");

            // Item Gun Tranq
            enableGunTranq = Config.Bind("Items", "EnableGunTranq", true, "Ativa ou desativa o drop de arma tranquilizante");
            gunTranqDropChance = Config.Bind("Items", "GunTranqDropChance", 0.05f, "Chance de um inimigo soltar arma tranquilizante (0-1)");
        }

        private void InitializeItems()
        {
            // Adiciona os itens com suas configurações
            // O prefixo "Item" é importante - verifica nos logs do jogo os nomes exatos dos prefabs
            string prefix = gameObjectPrefix.Value;

            var energy = new ItemDropInfo(
                "Upgrade Player Energy",
                enablePlayerEnergy.Value,
                playerEnergyDropChance.Value,
                $"{prefix} Upgrade Player Energy"
            );
            energy.AlternativeNames.AddRange(new[] {
                "Player Energy",
                "Energy Upgrade",
                "ItemPlayerEnergy",
                "Upgrade Player Stamina"
            });
            itemDrops.Add("Upgrade_Player_Energy", energy);

            var extraJump = new ItemDropInfo(
                "Upgrade Player Extra Jump",
                enablePlayerExtraJump.Value,
                playerExtraJumpDropChance.Value,
                $"{prefix} Upgrade Player Extra Jump"
            );
            extraJump.AlternativeNames.AddRange(new[] {
                "Player Extra Jump",
                "Extra Jump",
                "ItemPlayerExtraJump",
                "Jump Upgrade"
            });
            itemDrops.Add("Upgrade_Player_Extra_Jump", extraJump);

            var grabRange = new ItemDropInfo(
                "Upgrade Player Grab Range",
                enablePlayerGrabRange.Value,
                playerGrabRangeDropChance.Value,
                $"{prefix} Upgrade Player Grab Range"
            );
            grabRange.AlternativeNames.AddRange(new[] {
                "Player Grab Range",
                "Grab Range",
                "ItemPlayerGrabRange",
                "Range Upgrade"
            });
            itemDrops.Add("Upgrade_Player_Grab_Range", grabRange);

            var grabStrength = new ItemDropInfo(
                "Upgrade Player Grab Strength",
                enablePlayerGrabStrength.Value,
                playerGrabStrengthDropChance.Value,
                $"{prefix} Upgrade Player Grab Strength"
            );
            grabStrength.AlternativeNames.AddRange(new[] {
                "Player Grab Strength",
                "Grab Strength",
                "ItemPlayerGrabStrength",
                "Strength Upgrade"
            });
            itemDrops.Add("Upgrade_Player_Grab_Strength", grabStrength);

            var health = new ItemDropInfo(
                "Upgrade Player Health",
                enablePlayerHealth.Value,
                playerHealthDropChance.Value,
                $"{prefix} Upgrade Player Health"
            );
            health.AlternativeNames.AddRange(new[] {
                "Player Health",
                "Health Upgrade",
                "ItemPlayerHealth"
            });
            itemDrops.Add("Upgrade_Player_Health", health);

            var sprintSpeed = new ItemDropInfo(
                "Upgrade Player Sprint Speed",
                enablePlayerSprintSpeed.Value,
                playerSprintSpeedDropChance.Value,
                $"{prefix} Upgrade Player Sprint Speed"
            );
            sprintSpeed.AlternativeNames.AddRange(new[] {
                "Player Sprint Speed",
                "Sprint Speed",
                "ItemPlayerSprintSpeed",
                "Speed Upgrade"
            });
            itemDrops.Add("Upgrade_Player_Sprint_Speed", sprintSpeed);

            var tumbleLaunch = new ItemDropInfo(
                "Upgrade Player Tumble Launch",
                enablePlayerTumbleLaunch.Value,
                playerTumbleLaunchDropChance.Value,
                $"{prefix} Upgrade Player Tumble Launch"
            );
            tumbleLaunch.AlternativeNames.AddRange(new[] {
                "Player Tumble Launch",
                "Tumble Launch",
                "ItemPlayerTumbleLaunch",
                "Launch Upgrade"
            });
            itemDrops.Add("Upgrade_Player_Tumble_Launch", tumbleLaunch);

            var gunTranq = new ItemDropInfo(
                "Gun Tranq",
                enableGunTranq.Value,
                gunTranqDropChance.Value,
                $"{prefix} Gun Tranq"
            );
            gunTranq.AlternativeNames.AddRange(new[] {
                "Tranq Gun",
                "Tranquilizer",
                "ItemGunTranq"
            });
            itemDrops.Add("Gun_Tranq", gunTranq);

            Log.LogInfo($"Inicializados {itemDrops.Count} tipos de itens para drop");
        }

        #endregion

        #region Métodos Principais

        private string GetRandomDrop()
        {
            // Filtra itens habilitados
            var enabledItems = itemDrops.Where(item => item.Value.Enabled).ToList();

            if (enabledItems.Count == 0)
            {
                Log.LogWarning("Nenhum item está habilitado nas configurações");
                return null;
            }

            // Normaliza as chances
            float totalChance = enabledItems.Sum(item => item.Value.DropChance);
            float randomValue = UnityEngine.Random.Range(0f, totalChance);

            Log.LogInfo($"Rolando item: valor aleatório {randomValue} / total {totalChance}");

            float accumulatedChance = 0f;
            foreach (var item in enabledItems)
            {
                accumulatedChance += item.Value.DropChance;
                Log.LogInfo($"Item: {item.Key}, Chance: {item.Value.DropChance}, Acumulado: {accumulatedChance}");
                if (randomValue <= accumulatedChance)
                {
                    Log.LogInfo($"Item selecionado: {item.Key}");
                    return item.Key;
                }
            }

            // Caso padrão: retorna o primeiro item ou null
            if (enabledItems.Count > 0)
            {
                Log.LogInfo($"Item padrão selecionado: {enabledItems[0].Key}");
                return enabledItems[0].Key;
            }
            else
            {
                Log.LogWarning("Nenhum item disponível para drop (lista vazia de itens habilitados)");
                return null;
            }
        }

        private bool SpawnDropItem(Vector3 position, string itemKey)
        {
            if (string.IsNullOrEmpty(itemKey) || !itemDrops.ContainsKey(itemKey))
            {
                Log.LogWarning($"Item não encontrado: {itemKey}");
                return false;
            }

            ItemDropInfo itemInfo = itemDrops[itemKey];

            try
            {
                Log.LogInfo($"Iniciando spawn do item {itemInfo.ItemName} na posição {position}");

                // Calcula uma posição aleatória próxima
                Vector3 randomOffset = UnityEngine.Random.insideUnitSphere * UnityEngine.Random.Range(minDropDistance.Value, maxDropDistance.Value);
                randomOffset.y = 0.1f; // Garante que o item não fique enterrado no chão
                Vector3 spawnPosition = position + randomOffset;

                Log.LogInfo($"Posição final do item: {spawnPosition}");

                // MÉTODO DIRETO: Exatamente como o RepoAdminMenu faz
                try
                {
                    // Primeiro tentamos obter os itens do StatsManager
                    if (StatsManager.instance != null)
                    {
                        Log.LogInfo("Usando StatsManager para encontrar itens...");
                        Item itemToSpawn = null;

                        // Converte o nome do item para um formato que corresponda ao que o jogo usa
                        string searchName = itemInfo.ItemName;
                        string alternateSearchName = itemKey.Replace("_", " ");

                        // Busca todos os itens disponíveis
                        foreach (Item item in StatsManager.instance.GetItems())
                        {
                            string itemName = item.name.Replace("Item ", "");

                            // Compara com nosso nome de item
                            if (itemName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                                itemName.Equals(alternateSearchName, StringComparison.OrdinalIgnoreCase) ||
                                itemName.Contains(searchName) ||
                                itemName.Contains(alternateSearchName) ||
                                item.name.Contains(searchName) ||
                                item.name.Contains(alternateSearchName))
                            {
                                itemToSpawn = item;
                                Log.LogInfo($"Item encontrado no StatsManager: {item.name}");
                                break;
                            }

                            // Verifica nomes alternativos
                            foreach (string altName in itemInfo.AlternativeNames)
                            {
                                if (itemName.Contains(altName) || item.name.Contains(altName))
                                {
                                    itemToSpawn = item;
                                    Log.LogInfo($"Item encontrado no StatsManager por nome alternativo: {altName}");
                                    break;
                                }
                            }

                            if (itemToSpawn != null) break;
                        }

                        // Se encontramos o item, vamos spawná-lo
                        if (itemToSpawn != null)
                        {
                            if (SemiFunc.IsMultiplayer())
                            {
                                // Método para multiplayer - exatamente como no ItemUtil.spawnItem
                                Items.SpawnItem(itemToSpawn, spawnPosition, Quaternion.identity);
                                Log.LogInfo($"Item spawned via método multiplayer: {itemToSpawn.name}");
                            }
                            else
                            {
                                // Método para singleplayer - exatamente como no ItemUtil.spawnItem
                                UnityEngine.Object.Instantiate(itemToSpawn.prefab, spawnPosition, Quaternion.identity);
                                Log.LogInfo($"Item spawned via método singleplayer: {itemToSpawn.name}");
                            }
                            return true;
                        }
                        else
                        {
                            Log.LogWarning($"Item não encontrado no StatsManager: {itemInfo.ItemName}");

                            // Tenta encontrar itens por nome parcial
                            foreach (Item item in StatsManager.instance.GetItems())
                            {
                                Log.LogInfo($"Item disponível: {item.name}");

                                // Para forçar o teste, podemos tentar spawnar o primeiro item que encontrarmos
                                if (itemKey.Contains("Energy") && item.name.Contains("Energy"))
                                {
                                    if (SemiFunc.IsMultiplayer())
                                    {
                                        Items.SpawnItem(item, spawnPosition, Quaternion.identity);
                                    }
                                    else
                                    {
                                        UnityEngine.Object.Instantiate(item.prefab, spawnPosition, Quaternion.identity);
                                    }
                                    Log.LogInfo($"Spawned item relacionado: {item.name}");
                                    return true;
                                }
                                else if (itemKey.Contains("Health") && item.name.Contains("Health"))
                                {
                                    if (SemiFunc.IsMultiplayer())
                                    {
                                        Items.SpawnItem(item, spawnPosition, Quaternion.identity);
                                    }
                                    else
                                    {
                                        UnityEngine.Object.Instantiate(item.prefab, spawnPosition, Quaternion.identity);
                                    }
                                    Log.LogInfo($"Spawned item relacionado: {item.name}");
                                    return true;
                                }
                                else if (itemKey.Contains("Jump") && item.name.Contains("Jump"))
                                {
                                    if (SemiFunc.IsMultiplayer())
                                    {
                                        Items.SpawnItem(item, spawnPosition, Quaternion.identity);
                                    }
                                    else
                                    {
                                        UnityEngine.Object.Instantiate(item.prefab, spawnPosition, Quaternion.identity);
                                    }
                                    Log.LogInfo($"Spawned item relacionado: {item.name}");
                                    return true;
                                }
                                // Adicione mais verificações para outros tipos de itens
                            }
                        }
                    }
                    else
                    {
                        Log.LogWarning("StatsManager.instance é null");
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"Erro ao tentar usar o método direto: {ex.Message}");
                }

                // Método de Fallback - Tenta AccessTools do Harmony para acessar o ItemManager do jogo
                try
                {
                    Log.LogInfo("Tentando método Harmony para acessar ItemManager...");

                    // Usa Harmony para acessar o ItemManager via reflexão
                    var itemManagerType = AccessTools.TypeByName("REPOLib.ItemManager") ?? AccessTools.TypeByName("ItemManager");
                    if (itemManagerType != null)
                    {
                        var getAllItems = AccessTools.Method(itemManagerType, "GetAllItems") ?? AccessTools.Method(itemManagerType, "GetItems");
                        if (getAllItems != null)
                        {
                            var items = getAllItems.Invoke(null, null) as IEnumerable<object>;
                            if (items != null)
                            {
                                Log.LogInfo("Itens encontrados via Harmony");

                                foreach (var item in items)
                                {
                                    // Obtém o nome do item
                                    var nameProperty = AccessTools.Property(item.GetType(), "name");
                                    if (nameProperty != null)
                                    {
                                        string name = nameProperty.GetValue(item) as string;
                                        Log.LogInfo($"Item disponível: {name}");

                                        // Verifica se o nome corresponde ao que queremos
                                        if (name != null && (name.Contains(itemInfo.ItemName) || name.Contains(itemKey.Replace("_", " "))))
                                        {
                                            // Obtém o método de spawn
                                            var spawnMethod = AccessTools.Method(itemManagerType, "SpawnItem");
                                            if (spawnMethod != null)
                                            {
                                                spawnMethod.Invoke(null, new object[] { item, spawnPosition, Quaternion.identity });
                                                Log.LogInfo($"Item spawned via Harmony: {name}");
                                                return true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"Erro ao tentar usar método Harmony: {ex.Message}");
                }

                // Método último recurso - Usa o código do RepoAdminMenu diretamente
                try
                {
                    Log.LogInfo("Tentando spawnar qualquer item upgrade como último recurso...");
                    foreach (Item item in StatsManager.instance.GetItems())
                    {
                        // Spawna qualquer upgrade que encontrar como teste
                        if (item.name.Contains("Upgrade"))
                        {
                            if (SemiFunc.IsMultiplayer())
                            {
                                Items.SpawnItem(item, spawnPosition, Quaternion.identity);
                            }
                            else
                            {
                                UnityEngine.Object.Instantiate(item.prefab, spawnPosition, Quaternion.identity);
                            }
                            Log.LogInfo($"Spawned item genérico: {item.name}");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"Erro ao tentar spawnar item genérico: {ex.Message}");
                }

                // Fallback para placeholder se nada funcionar
                Log.LogWarning("Todos os métodos falharam, criando placeholder...");
                GameObject placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholder.transform.position = spawnPosition;
                placeholder.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                placeholder.GetComponent<Renderer>().material.color = new Color(1f, 0.5f, 0f); // Laranja para indicar erro

                var textObj = new GameObject("ItemText");
                textObj.transform.SetParent(placeholder.transform);
                textObj.transform.localPosition = new Vector3(0, 0.6f, 0);
                textObj.transform.localRotation = Quaternion.identity;

                var textMesh = textObj.AddComponent<TextMesh>();
                textMesh.text = "ERRO: " + itemInfo.ItemName;
                textMesh.fontSize = 20;
                textMesh.alignment = TextAlignment.Center;
                textMesh.anchor = TextAnchor.LowerCenter;
                textMesh.color = Color.red;

                return false;
            }
            catch (Exception ex)
            {
                Log.LogError($"Erro geral ao criar item {itemInfo.ItemName}: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        public void DropItemAtPosition(Vector3 position)
        {
            // Método público para ser chamado diretamente dos patches
            if (!modEnabled.Value)
            {
                Log.LogInfo("Mod desativado nas configurações");
                return;
            }

            // Verifica se deve dropar item
            float randomChance = UnityEngine.Random.value;
            Log.LogInfo($"Verificando drop: {randomChance} <= {globalDropChance.Value}");

            if (randomChance <= globalDropChance.Value)
            {
                string itemToDrop = GetRandomDrop();
                if (!string.IsNullOrEmpty(itemToDrop))
                {
                    Log.LogInfo($"Dropando item {itemToDrop} na posição {position}");
                    SpawnDropItem(position, itemToDrop);
                }
            }
            else
            {
                Log.LogInfo("Sem drop desta vez");
            }
        }

        private void DiscoverItemsInGame()
        {
            Log.LogInfo("=== INICIANDO DESCOBERTA DE ITENS NO JOGO ===");

            try
            {
                // Procurar por GameObjects de itens na cena atual
                Log.LogInfo("Procurando GameObjects de itens na cena...");
                GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();

                int itemCount = 0;

                // Primeiro, procura por objetos com nome exato correspondente aos nossos itens
                foreach (var itemEntry in itemDrops)
                {
                    string itemKey = itemEntry.Key;
                    ItemDropInfo info = itemEntry.Value;

                    foreach (GameObject obj in allObjects)
                    {
                        if (obj.name.Equals(info.ItemName) ||
                            obj.name.Equals(info.PrefabPath) ||
                            obj.name.Equals(itemKey.Replace("_", " ")))
                        {
                            Log.LogInfo($"Item encontrado com correspondência exata: {obj.name}");

                            // Adiciona ao cache de prefabs
                            if (!cachedItemPrefabs.ContainsKey(itemKey))
                            {
                                cachedItemPrefabs.Add(itemKey, obj);
                                Log.LogInfo($"Item '{itemKey}' adicionado ao cache de prefabs");
                            }

                            itemCount++;
                        }
                    }
                }

                // Depois, procura por objetos que contenham partes dos nomes (menos preciso)
                if (itemCount == 0)
                {
                    Log.LogInfo("Nenhuma correspondência exata encontrada. Buscando correspondências parciais...");

                    foreach (var itemEntry in itemDrops)
                    {
                        string itemKey = itemEntry.Key;
                        ItemDropInfo info = itemEntry.Value;

                        if (cachedItemPrefabs.ContainsKey(itemKey))
                            continue; // Já encontrado na etapa anterior

                        // Gera palavras-chave para buscar nos nomes dos objetos
                        List<string> keywords = new List<string>();
                        keywords.Add(itemKey.Replace("_", ""));
                        keywords.Add(info.ItemName.Replace(" ", ""));
                        keywords.AddRange(info.AlternativeNames.Select(n => n.Replace(" ", "")));

                        foreach (GameObject obj in allObjects)
                        {
                            string objNameNoSpaces = obj.name.Replace(" ", "");

                            foreach (string keyword in keywords)
                            {
                                if (objNameNoSpaces.Contains(keyword))
                                {
                                    Log.LogInfo($"Item encontrado com correspondência parcial: {obj.name} (keyword: {keyword})");

                                    // Adiciona ao cache de prefabs
                                    if (!cachedItemPrefabs.ContainsKey(itemKey))
                                    {
                                        cachedItemPrefabs.Add(itemKey, obj);
                                        Log.LogInfo($"Item '{itemKey}' adicionado ao cache de prefabs (correspondência parcial)");
                                    }

                                    itemCount++;
                                    break;
                                }
                            }
                        }
                    }
                }

                Log.LogInfo($"Total de {itemCount} itens encontrados e armazenados em cache");
                Log.LogInfo($"Cache atual contém {cachedItemPrefabs.Count} prefabs de itens");
            }
            catch (Exception ex)
            {
                Log.LogError($"Erro ao descobrir itens: {ex.Message}");
            }

            Log.LogInfo("=== DESCOBERTA DE ITENS CONCLUÍDA ===");
        }

        // Método para teste via console
        public void TrySpawnPredefinedItems()
        {
            Log.LogInfo("Tentando fazer spawn de itens predefinidos para teste...");

            // Posição à frente do jogador ou no centro da tela
            Vector3 spawnPosition = Vector3.zero;
            if (Camera.main != null)
            {
                spawnPosition = Camera.main.transform.position + Camera.main.transform.forward * 2f;
            }

            // Testa cada item definido
            foreach (var item in itemDrops)
            {
                if (item.Value.Enabled)
                {
                    Log.LogInfo($"Testando spawn de: {item.Key}");
                    SpawnDropItem(spawnPosition, item.Key);

                    // Incrementa a posição para o próximo item
                    spawnPosition += new Vector3(1f, 0, 0);
                }
            }

            Log.LogInfo("Testes de spawn concluídos");
        }

        #endregion

        #region Classes Auxiliares

        // Componente para identificar o tipo de item nos placeholders
        private class ItemIdentifier : MonoBehaviour
        {
            public string itemKey;
            public string itemName;

            // Adiciona cor distinta baseada no tipo de item
            private void Start()
            {
                Renderer renderer = GetComponent<Renderer>();
                if (renderer != null)
                {
                    if (itemKey.Contains("Energy"))
                        renderer.material.color = new Color(0.2f, 0.8f, 1f); // Azul para energia
                    else if (itemKey.Contains("Jump"))
                        renderer.material.color = new Color(0.2f, 1f, 0.2f); // Verde para pulo
                    else if (itemKey.Contains("Health"))
                        renderer.material.color = new Color(1f, 0.2f, 0.2f); // Vermelho para saúde
                    else if (itemKey.Contains("Gun") || itemKey.Contains("Tranq"))
                        renderer.material.color = new Color(0.8f, 0.8f, 0.2f); // Amarelo para armas
                    else
                        renderer.material.color = new Color(0.8f, 0.4f, 1f); // Roxo para outros
                }
            }
        }

        #endregion

        #region Harmony Patches e Controle de Drops

        // Método para verificar e registrar inimigos que já droparam
        private static bool HasRecentlyDropped(EnemyHealth enemy)
        {
            // Limpa inimigos antigos para evitar vazamento de memória
            float currentTime = Time.time;
            List<int> keysToRemove = new List<int>();

            foreach (var pair in recentDrops)
            {
                if (currentTime - pair.Value > dropCooldown)
                {
                    keysToRemove.Add(pair.Key);
                }
            }

            foreach (int key in keysToRemove)
            {
                recentDrops.Remove(key);
            }

            // Verifica se o inimigo já dropou recentemente
            int enemyId = enemy.GetInstanceID();
            if (recentDrops.ContainsKey(enemyId))
            {
                return true; // Já dropou recentemente
            }

            // Registra o inimigo
            recentDrops[enemyId] = currentTime;
            return false; // Não dropou recentemente
        }

        // Define patches específicos para os métodos que vimos nos logs do jogo
        [HarmonyPatch(typeof(EnemyHealth), "Death")]
        public static class EnemyHealthDeathPatch
        {
            public static void Postfix(EnemyHealth __instance)
            {
                try
                {
                    if (__instance != null)
                    {
                        Log.LogInfo($"EnemyHealth.Death chamado");

                        // Verifica se o inimigo já dropou recentemente
                        if (HasRecentlyDropped(__instance))
                        {
                            Log.LogInfo("Este inimigo já dropou um item recentemente, ignorando");
                            return;
                        }

                        Vector3 position = __instance.transform.position;
                        Instance.DropItemAtPosition(position);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"Erro no patch EnemyHealth.Death: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(EnemyHealth), "DeathRPC")]
        public static class EnemyHealthDeathRPCPatch
        {
            public static void Postfix(EnemyHealth __instance)
            {
                try
                {
                    if (__instance != null)
                    {
                        Log.LogInfo($"EnemyHealth.DeathRPC chamado");

                        // Verifica se o inimigo já dropou recentemente
                        if (HasRecentlyDropped(__instance))
                        {
                            Log.LogInfo("Este inimigo já dropou um item recentemente, ignorando");
                            return;
                        }

                        Vector3 position = __instance.transform.position;
                        Instance.DropItemAtPosition(position);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"Erro no patch EnemyHealth.DeathRPC: {ex.Message}");
                }
            }
        }

        #endregion
    }
}