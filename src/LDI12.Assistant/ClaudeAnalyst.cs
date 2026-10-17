using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using LDI12.Core.Model;

namespace LDI12.Assistant
{
    /// <summary>
    /// Claude, au travail en arrière-plan : il lit le diagnostic, cherche au besoin sur le web,
    /// et rend un plan fait d'opérations de LDI12.
    /// </summary>
    /// <remarks>
    /// <b>Aucune conversation.</b> Le technicien ne voit ni Claude ni sa console : il a posé le
    /// problème en quelques choix, l'analyse tourne, un plan revient. Claude ne pose pas de
    /// question ; ce qu'il ne sait pas, il le lit dans le relevé, et ce que le relevé ne dit pas,
    /// il le signale comme incertain.
    /// <para>
    /// <b>Rien ne touche la machine ici.</b> Les outils offerts à Claude ne font que lire le
    /// relevé déjà fait. Le plan désigne des opérations de LDI12 par leur identifiant ; c'est
    /// l'écran qui les montre, et le technicien qui les lance, d'un clic, avec les mêmes relevés
    /// préalables et le même journal que s'il les lançait lui-même.
    /// </para>
    /// </remarks>
    public sealed class ClaudeAnalyst
    {
        public const string Model = "claude-opus-5-5";

        private const int MaxTurns = 16;

        private readonly AnthropicClient _client;

        public ClaudeAnalyst(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Clé vide.", nameof(apiKey));
            _client = new AnthropicClient { ApiKey = apiKey.Trim(), MaxRetries = 3, Timeout = TimeSpan.FromMinutes(5) };
        }

        /// <summary>Vérifie la clé sans rien consommer : la fiche du modèle se lit gratuitement.</summary>
        public async Task CheckAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _client.Models.Retrieve(Model, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Translate(ex) is { } message)
            {
                throw new AssistantException(message, ex);
            }
        }

        public async Task<AssistantPlan> AnalyseAsync(
            SystemSnapshot snapshot, AssistantRequest request, IReadOnlyList<AvailableAction> actions,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (request == null) throw new ArgumentNullException(nameof(request));
            actions ??= Array.Empty<AvailableAction>();

            var known = new HashSet<string>(actions.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            var messages = new List<BetaMessageParam> { new() { Role = Role.User, Content = Task(request) } };
            long input = 0, output = 0;
            var searches = 0;

            var tools = new List<BetaToolUnion> { ReadTool(), PlanTool() };
            // La recherche web sert surtout aux pilotes : versions publiées par le constructeur,
            // problèmes connus d'une version. Bornée : une analyse ne doit pas devenir une enquête.
            tools.Insert(0, new BetaWebSearchTool20260209 { MaxUses = request.Mode == AssistantMode.Drivers ? 6 : 3 });

            progress?.Report("Lecture du diagnostic…");

            for (var turn = 0; turn < MaxTurns; turn++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                BetaMessage response;
                try
                {
                    response = await _client.Beta.Messages.Create(new MessageCreateParams
                    {
                        Model = Model,
                        MaxTokens = 16000,
                        Betas = new List<ApiEnum<string, AnthropicBeta>> { AnthropicBeta.ServerSideFallback2026_07_01 },
                        Fallbacks = new Default(),
                        OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                        System = new List<BetaTextBlockParam>
                        {
                            new() { Text = SystemPrompt(actions), CacheControl = new BetaCacheControlEphemeral() },
                        },
                        Tools = tools,
                        Messages = messages,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) && Translate(ex) is { } message)
                {
                    throw new AssistantException(message, ex);
                }

                input += response.Usage.InputTokens;
                output += response.Usage.OutputTokens;

                if (response.StopReason == BetaStopReason.Refusal)
                    throw new AssistantException("Cette analyse a été refusée. Reformulez le problème, ou passez par les réparations.");

                var assistant = new List<BetaContentBlockParam>();
                var results = new List<BetaContentBlockParam>();
                AssistantPlan? plan = null;

                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var text))
                    {
                        assistant.Add(new BetaTextBlockParam { Text = text.Text });
                    }
                    else if (block.TryPickThinking(out var thinking))
                    {
                        assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out var redacted))
                    {
                        assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickServerToolUse(out var server))
                    {
                        searches++;
                        progress?.Report("Recherche sur le web : " + Describe(server.Input, "query"));
                        assistant.Add(new BetaServerToolUseBlockParam
                        {
                            ID = server.ID,
                            Name = BetaServerToolUseBlockParamName.WebSearch,
                            Input = server.Input,
                        });
                    }
                    else if (block.TryPickWebSearchToolResult(out var search))
                    {
                        assistant.Add(new BetaWebSearchToolResultBlockParam { ToolUseID = search.ToolUseID, Content = Convert(search.Content) });
                    }
                    else if (block.TryPickToolUse(out var use))
                    {
                        assistant.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });

                        if (use.Name == "submit_plan")
                        {
                            plan = ParsePlan(use.Input, known);
                            results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = "Plan reçu." });
                            continue;
                        }

                        var section = Describe(use.Input, "section");
                        var query = Describe(use.Input, "query");
                        progress?.Report("Lecture : " + Label(section) + (query.Length > 0 ? " (« " + query + " »)" : string.Empty));
                        results.Add(new BetaToolResultBlockParam
                        {
                            ToolUseID = use.ID,
                            Content = use.Name == "read_machine_data"
                                ? MachineData.Read(snapshot, section, query.Length > 0 ? query : null)
                                : "Outil inconnu.",
                        });
                    }
                }

                if (plan != null)
                {
                    progress?.Report("Plan prêt.");
                    return new AssistantPlan
                    {
                        Summary = plan.Summary,
                        Confidence = plan.Confidence,
                        Findings = plan.Findings,
                        Steps = plan.Steps,
                        InputTokens = input,
                        OutputTokens = output,
                        WebSearches = searches,
                    };
                }

                messages.Add(new() { Role = Role.Assistant, Content = assistant });

                // Recherche web longue : le serveur rend la main, on le relance tel quel.
                if (response.StopReason == BetaStopReason.PauseTurn) continue;

                if (results.Count > 0)
                {
                    messages.Add(new() { Role = Role.User, Content = results });
                }
                else
                {
                    // Une réponse en texte libre : le plan ne passe que par submit_plan.
                    messages.Add(new() { Role = Role.User, Content = "Termine en appelant submit_plan avec ton plan." });
                }
            }

            throw new AssistantException("L'analyse n'a pas abouti dans le nombre d'étapes prévu. Relancez-la, ou précisez le problème.");
        }

        private static string Task(AssistantRequest request)
        {
            var text = new StringBuilder();
            if (request.Mode == AssistantMode.Drivers)
            {
                text.AppendLine("Analyse les pilotes de cette machine par rapport à son matériel.");
                text.AppendLine("Repère : périphériques sans pilote ou en erreur, pilotes génériques Microsoft là où le constructeur en fournit un,");
                text.AppendLine("pilotes très anciens ou connus pour poser problème (carte graphique, chipset, stockage, réseau, Wi-Fi, audio),");
                text.AppendLine("pilotes non signés, et ce que les journaux (écrans bleus, erreurs) attribuent à un pilote.");
                text.AppendLine("Vérifie sur le web les versions récentes publiées par le constructeur pour les pilotes importants.");
            }
            else
            {
                text.AppendLine("Diagnostique le problème décrit par le technicien.");
                text.AppendLine("Problème : " + (string.IsNullOrWhiteSpace(request.Problem) ? "non précisé" : request.Problem));
                if (request.Details.Count > 0) text.AppendLine("Précisions : " + string.Join(" ; ", request.Details));
            }

            if (!string.IsNullOrWhiteSpace(request.Notes))
                text.AppendLine("Ce que dit le client : « " + request.Notes!.Trim() + " »");

            text.AppendLine("Lis les sections utiles du diagnostic, puis appelle submit_plan.");
            return text.ToString();
        }

        private static string SystemPrompt(IReadOnlyList<AvailableAction> actions)
        {
            var text = new StringBuilder();
            text.AppendLine("Tu es l'assistant de diagnostic de LDI12 Diagnostic, le logiciel d'un dépanneur informatique.");
            text.AppendLine("Tu travailles en arrière-plan : personne ne lit tes messages, aucun humain ne peut te répondre.");
            text.AppendLine("Ne pose aucune question. Ce que tu ne peux pas établir, dis-le dans le plan, avec ta confiance.");
            text.AppendLine();
            text.AppendLine("Tes sources : l'outil read_machine_data (le relevé complet que LDI12 vient de faire de la machine),");
            text.AppendLine("et la recherche web pour les versions de pilotes et les problèmes connus. N'invente aucune donnée de la machine.");
            text.AppendLine();
            text.AppendLine("Ton rendu : un seul appel à submit_plan. Le technicien voit le plan, et le lance d'un clic.");
            text.AppendLine("- En français, phrases courtes et concrètes, pour un technicien. Pas de jargon inutile.");
            text.AppendLine("- Étapes dans l'ordre où les faire, les moins risquées d'abord. Pas plus de huit.");
            text.AppendLine("- Une étape automatique désigne une opération de LDI12 par son identifiant exact, pris dans la liste ci-dessous.");
            text.AppendLine("- Ce qu'aucune opération ne fait (télécharger un pilote chez le constructeur, changer une pièce, un réglage du BIOS)");
            text.AppendLine("  devient une étape manuelle : action_id vide, et dans « manual » le geste précis. Pour un téléchargement,");
            text.AppendLine("  donne le site officiel du constructeur trouvé par ta recherche, jamais une adresse supposée.");
            text.AppendLine("- Ne propose une réinstallation de Windows que si le relevé la justifie clairement, et dis pourquoi.");
            text.AppendLine();
            text.AppendLine("Opérations de LDI12 disponibles sur cette machine :");
            foreach (var action in actions)
            {
                text.Append("- ").Append(action.Id).Append(" : ").Append(action.Title).Append(". ").Append(action.Purpose)
                    .Append(" Risque : ").Append(action.Risk).Append('.');
                if (!string.IsNullOrEmpty(action.Parameter)) text.Append(" Paramètre : ").Append(action.Parameter).Append('.');
                text.AppendLine();
            }
            return text.ToString();
        }

        private static BetaTool ReadTool() => new()
        {
            Name = "read_machine_data",
            Description = "Lit une section du relevé que LDI12 a fait de la machine, en JSON. "
                          + "Pour les listes (devices, drivers, services), « query » garde les seuls éléments qui contiennent ce texte.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["section"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        @enum = MachineData.Sections.Keys.ToArray(),
                        description = string.Join(" ; ", MachineData.Sections.Select(s => s.Key + " : " + s.Value)),
                    }),
                    ["query"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "Filtre facultatif, par exemple « nvidia », « Realtek » ou « wlan ».",
                    }),
                },
                Required = new List<string> { "section" },
            },
        };

        private static BetaTool PlanTool() => new()
        {
            Name = "submit_plan",
            Description = "Rend le diagnostic et le plan au technicien. À appeler une seule fois, pour terminer.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["summary"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "Deux ou trois phrases : ce qui se passe, et ce que le plan va régler.",
                    }),
                    ["confidence"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "faible", "moyenne", "forte" } }),
                    ["findings"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string" },
                                detail = new { type = "string", description = "Le fait relevé, avec sa valeur : version, date, code d'erreur." },
                                severity = new { type = "string", @enum = new[] { "info", "attention", "problème", "critique" } },
                            },
                            required = new[] { "title", "detail", "severity" },
                        },
                    }),
                    ["steps"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string", description = "Le geste, en quelques mots." },
                                why = new { type = "string", description = "Pourquoi cette étape, en une phrase." },
                                action_id = new { type = "string", description = "Identifiant exact d'une opération de LDI12, ou chaîne vide pour une étape manuelle." },
                                parameters = new
                                {
                                    type = "object",
                                    description = "Paramètres de l'opération, s'il en faut (par exemple {\"service\": \"Spooler\"}).",
                                    additionalProperties = new { type = "string" },
                                },
                                manual = new { type = "string", description = "Pour une étape manuelle : le geste précis, et le site officiel s'il faut télécharger." },
                            },
                            required = new[] { "title", "why", "action_id" },
                        },
                    }),
                },
                Required = new List<string> { "summary", "confidence", "findings", "steps" },
            },
        };

        /// <summary>Le plan tel que Claude l'a rendu ; une opération inconnue devient une étape manuelle, jamais une exécution.</summary>
        internal static AssistantPlan ParsePlan(IReadOnlyDictionary<string, JsonElement> input, ISet<string> known)
        {
            string Text(JsonElement element, string name)
                => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            var findings = new List<PlanFinding>();
            if (input.TryGetValue("findings", out var findingArray) && findingArray.ValueKind == JsonValueKind.Array)
                foreach (var item in findingArray.EnumerateArray())
                    findings.Add(new PlanFinding { Title = Text(item, "title"), Detail = Text(item, "detail"), Severity = Text(item, "severity") });

            var steps = new List<PlanStep>();
            if (input.TryGetValue("steps", out var stepArray) && stepArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in stepArray.EnumerateArray())
                {
                    var id = Text(item, "action_id").Trim();
                    var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("parameters", out var map) && map.ValueKind == JsonValueKind.Object)
                        foreach (var property in map.EnumerateObject())
                            if (property.Value.ValueKind == JsonValueKind.String) parameters[property.Name] = property.Value.GetString() ?? string.Empty;

                    var manual = Text(item, "manual");
                    var isKnown = id.Length > 0 && known.Contains(id);
                    if (id.Length > 0 && !isKnown && manual.Length == 0)
                        manual = "Opération proposée inconnue de LDI12 (« " + id + " ») : à faire à la main si elle a un sens.";

                    steps.Add(new PlanStep
                    {
                        Title = Text(item, "title"),
                        Why = Text(item, "why"),
                        ActionId = isKnown ? known.First(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)) : null,
                        Parameters = parameters,
                        Manual = manual.Length > 0 ? manual : null,
                    });
                }
            }

            return new AssistantPlan
            {
                Summary = input.TryGetValue("summary", out var summary) && summary.ValueKind == JsonValueKind.String ? summary.GetString() ?? string.Empty : string.Empty,
                Confidence = input.TryGetValue("confidence", out var confidence) && confidence.ValueKind == JsonValueKind.String ? confidence.GetString() ?? "moyenne" : "moyenne",
                Findings = findings,
                Steps = steps,
            };
        }

        private static BetaWebSearchToolResultBlockParamContent Convert(BetaWebSearchToolResultBlockContent content)
        {
            if (!content.TryPickBetaWebSearchResultBlocks(out var blocks))
                return new BetaWebSearchToolRequestError { ErrorCode = ((BetaWebSearchToolResultError)content.Value!).ErrorCode };

            var results = new List<BetaWebSearchResultBlockParam>();
            foreach (var result in blocks)
                results.Add(new BetaWebSearchResultBlockParam
                {
                    Url = result.Url,
                    Title = result.Title,
                    EncryptedContent = result.EncryptedContent,
                    PageAge = result.PageAge,
                });
            return results;
        }

        private static string Describe(IReadOnlyDictionary<string, JsonElement> input, string name)
            => input.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        private static string Label(string section) => section switch
        {
            "machine" => "la machine",
            "hardware" => "le matériel",
            "storage" => "les disques",
            "devices" => "les périphériques",
            "drivers" => "les pilotes",
            "events" => "les journaux",
            "services" => "les services",
            "startup" => "le démarrage",
            "windows" => "Windows",
            "network" => "le réseau",
            "security" => "la sécurité",
            "performance" => "les performances",
            "findings" => "les constats",
            _ => section,
        };

        /// <summary>Une erreur d'API en une phrase utile ; null pour ce qui n'en est pas une.</summary>
        private static string? Translate(Exception ex) => ex switch
        {
            AnthropicUnauthorizedException => "La clé d'API est refusée. Vérifiez-la dans la console Anthropic.",
            AnthropicForbiddenException => "Cette clé n'a pas accès au modèle.",
            AnthropicRateLimitException => "Trop de demandes pour l'instant. Réessayez dans une minute.",
            AnthropicBadRequestException bad when bad.Message.IndexOf("credit", StringComparison.OrdinalIgnoreCase) >= 0
                => "Le compte Anthropic n'a plus de crédit.",
            Anthropic5xxException => "Le service d'analyse est momentanément indisponible. Réessayez dans un instant.",
            AnthropicIOException => "Service d'analyse injoignable : vérifiez la connexion à Internet.",
            AnthropicApiException api => "Le service d'analyse a répondu par une erreur : " + api.Message,
            AnthropicException other => "Échange avec le service d'analyse impossible : " + other.Message,
            System.Net.Http.HttpRequestException => "Service d'analyse injoignable : vérifiez la connexion à Internet.",
            TaskCanceledException => "Le service d'analyse n'a pas répondu à temps.",
            _ => null,
        };
    }
}
