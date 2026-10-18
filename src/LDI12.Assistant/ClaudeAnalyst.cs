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
    /// <summary>Ce que l'analyse peut relire à l'instant sur la machine : ce que le diagnostic ne fige pas.</summary>
    public interface ILiveMachine
    {
        /// <summary>« audio », « network », « device » (cible : identifiant), « service » (cible : nom court).</summary>
        string Read(string what, string? target);
    }

    /// <summary>
    /// Une séance de dépannage avec Claude, menée en arrière-plan, étape par étape.
    /// </summary>
    /// <remarks>
    /// <b>Pas une conversation : des étapes.</b> Le technicien ne tape rien à Claude et ne lit
    /// aucune réponse en prose. Chaque étape revient structurée (hypothèses, opérations à lancer,
    /// vérifications à faire avec leurs réponses toutes prêtes, gestes manuels), l'écran en fait
    /// des cartes et des boutons, et ce que le technicien a fait et constaté repart vers Claude
    /// d'un clic sur « Continuer ». Sous le capot, chaque étape est la réponse à l'outil
    /// submit_step : le compte rendu du technicien est le résultat de cet outil, si bien que la
    /// séance reste une seule suite de messages, mise en cache d'un tour sur l'autre.
    /// <para>
    /// <b>Rien ne touche la machine ici.</b> Les outils offerts à Claude ne font que lire : le
    /// relevé du diagnostic, et quelques états relus à l'instant (son, réseau, un périphérique,
    /// un service). Les opérations du plan passent par l'écran, et le clic du technicien.
    /// </para>
    /// </remarks>
    public sealed class ClaudeAnalyst
    {
        public const string Model = "claude-opus-5-5";

        /// <summary>Lectures au plus par étape, avant que Claude ne rende la sienne.</summary>
        private const int MaxTurnsPerRound = 6;

        /// <summary>Au-delà, la séance s'arrête : le problème demande l'atelier, pas une étape de plus.</summary>
        public const int MaxRounds = 8;

        // Tarif public de Claude Opus 5.5, en dollars par million de jetons, et par recherche web.
        private const double InputPrice = 4.0;
        private const double CacheWritePrice = 5.0;
        private const double CacheReadPrice = 0.20;
        private const double OutputPrice = 20.0;
        private const double SearchPrice = 0.01;

        private readonly AnthropicClient _client;
        private readonly List<BetaMessageParam> _messages = new List<BetaMessageParam>();

        /// <summary>Les résultats d'outils de la dernière réponse, en attente du compte rendu de l'étape.</summary>
        private List<BetaContentBlockParam>? _pending;
        private string? _stepToolId;

        private SystemSnapshot? _snapshot;
        private IReadOnlyList<AvailableAction> _actions = Array.Empty<AvailableAction>();
        private HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ILiveMachine? _live;
        private bool _drivers;
        private long _input, _output, _cacheWrite, _cacheRead;
        private int _searches, _round;

        public ClaudeAnalyst(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Clé vide.", nameof(apiKey));
            _client = new AnthropicClient { ApiKey = apiKey.Trim(), MaxRetries = 3, Timeout = TimeSpan.FromMinutes(5) };
        }

        public int Round => _round;

        public bool CanContinue => _stepToolId != null && _round < MaxRounds;

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

        /// <summary>Première étape : la demande, la fiche de la machine, et ce qui a été relu à l'instant.</summary>
        public Task<AssistantPlan> StartAsync(
            SystemSnapshot snapshot, AssistantRequest request, IReadOnlyList<AvailableAction> actions, ILiveMachine? live,
            IProgress<string>? progress, CancellationToken cancellationToken)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            if (request == null) throw new ArgumentNullException(nameof(request));
            _actions = actions ?? Array.Empty<AvailableAction>();
            _known = new HashSet<string>(_actions.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            _live = live;
            _drivers = request.Mode == AssistantMode.Drivers;

            var first = Task(request) + "\n" + MachineBrief.Compose(snapshot, request.Mode);
            if (!string.IsNullOrWhiteSpace(request.LiveContext)) first += "\n## Relevé à l'instant\n" + request.LiveContext;
            _messages.Clear();
            _messages.Add(new() { Role = Role.User, Content = first });
            progress?.Report("Lecture du diagnostic…");
            return RunAsync(progress, cancellationToken);
        }

        /// <summary>Étape suivante : le compte rendu de la précédente part comme résultat de son submit_step.</summary>
        public async Task<AssistantPlan> ContinueAsync(StepFeedback feedback, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (!CanContinue || _pending == null) throw new AssistantException("La séance est terminée : relancez une nouvelle analyse.");

            var results = _pending
                .Select(block => block.TryPickToolResult(out var result) && result.ToolUseID == _stepToolId
                    ? new BetaToolResultBlockParam { ToolUseID = _stepToolId!, Content = feedback.Render(_round) }
                    : block)
                .ToList();
            // En cas d'échec (réseau, quota, annulation), la séance revient à l'étape rendue : le
            // technicien peut réessayer la suite sans tout reprendre.
            var count = _messages.Count;
            var pending = _pending;
            var stepToolId = _stepToolId;

            _messages.Add(new() { Role = Role.User, Content = results });
            _pending = null;
            _stepToolId = null;
            progress?.Report("Analyse des résultats de l'étape " + _round + "…");
            try
            {
                return await RunAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _messages.RemoveRange(count, _messages.Count - count);
                _pending = pending;
                _stepToolId = stepToolId;
                throw;
            }
        }

        private async Task<AssistantPlan> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var tools = new List<BetaToolUnion>
            {
                new BetaWebSearchTool20260209 { MaxUses = _drivers ? 3 : 2 },
                ReadTool(),
                LiveTool(),
                StepTool(),
            };

            for (var turn = 0; turn < MaxTurnsPerRound; turn++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                BetaMessage response;
                try
                {
                    response = await _client.Beta.Messages.Create(new MessageCreateParams
                    {
                        Model = Model,
                        MaxTokens = 8000,
                        Betas = new List<ApiEnum<string, AnthropicBeta>> { AnthropicBeta.ServerSideFallback2026_07_01 },
                        Fallbacks = new Default(),
                        // Les pilotes se jugent sur des faits (versions, dates, codes) : un effort
                        // modeste suffit. Une panne décrite en quelques mots demande davantage de raisonnement.
                        OutputConfig = new BetaOutputConfig { Effort = _drivers ? Effort.Low : Effort.Medium },
                        // Cache placé à chaque tour sur le dernier bloc : les tours et les étapes suivantes
                        // relisent la séance au vingtième du prix au lieu de la repayer entière.
                        CacheControl = new BetaCacheControlEphemeral(),
                        System = new List<BetaTextBlockParam>
                        {
                            new() { Text = SystemPrompt(_actions), CacheControl = new BetaCacheControlEphemeral() },
                        },
                        Tools = tools,
                        Messages = _messages,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) && Translate(ex) is { } message)
                {
                    throw new AssistantException(message, ex);
                }

                _input += response.Usage.InputTokens;
                _output += response.Usage.OutputTokens;
                _cacheWrite += response.Usage.CacheCreationInputTokens ?? 0;
                _cacheRead += response.Usage.CacheReadInputTokens ?? 0;

                if (response.StopReason == BetaStopReason.Refusal)
                    throw new AssistantException("Cette analyse a été refusée. Reformulez le problème, ou passez par les réparations.");

                var assistant = new List<BetaContentBlockParam>();
                var results = new List<BetaContentBlockParam>();
                AssistantPlan? step = null;
                string? stepId = null;

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
                        _searches++;
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

                        if (use.Name == "submit_step")
                        {
                            step = ParseStep(use.Input, _known);
                            stepId = use.ID;
                            // Le résultat de cet outil sera le compte rendu du technicien, à l'étape suivante.
                            results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = "En attente." });
                            continue;
                        }

                        results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = Execute(use.Name, use.Input, progress) });
                    }
                }

                _messages.Add(new() { Role = Role.Assistant, Content = assistant });

                if (step != null)
                {
                    _round++;
                    _pending = results;
                    _stepToolId = stepId;
                    progress?.Report("Étape " + _round + " prête.");
                    return new AssistantPlan
                    {
                        Round = _round,
                        Status = step.Status,
                        Summary = step.Summary,
                        Confidence = step.Confidence,
                        Hypotheses = step.Hypotheses,
                        Findings = step.Findings,
                        Steps = step.Steps,
                        Checks = step.Checks,
                        Next = step.Next,
                        InputTokens = _input + _cacheWrite + _cacheRead,
                        OutputTokens = _output,
                        WebSearches = _searches,
                        CostDollars = (_input * InputPrice + _cacheWrite * CacheWritePrice + _cacheRead * CacheReadPrice + _output * OutputPrice) / 1_000_000
                                      + _searches * SearchPrice,
                    };
                }

                // Recherche web longue : le serveur rend la main, on le relance tel quel.
                if (response.StopReason == BetaStopReason.PauseTurn) continue;

                _messages.Add(results.Count > 0
                    ? new BetaMessageParam { Role = Role.User, Content = results }
                    : new BetaMessageParam { Role = Role.User, Content = "Rends ton étape avec submit_step." });
            }

            throw new AssistantException("L'analyse n'a pas abouti dans le nombre de lectures prévu. Relancez-la, ou précisez le problème.");
        }

        private string Execute(string tool, IReadOnlyDictionary<string, JsonElement> input, IProgress<string>? progress)
        {
            if (tool == "read_machine_data")
            {
                var section = Describe(input, "section");
                var query = Describe(input, "query");
                progress?.Report("Lecture : " + Label(section) + (query.Length > 0 ? " (« " + query + " »)" : string.Empty));
                return MachineData.Read(_snapshot!, section, query.Length > 0 ? query : null);
            }

            if (tool == "read_live")
            {
                var what = Describe(input, "what");
                var target = Describe(input, "target");
                progress?.Report("Relevé à l'instant : " + LiveLabel(what));
                if (_live == null) return "Relevé à l'instant indisponible.";
                try { return _live.Read(what, target.Length > 0 ? target : null); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { return "Relevé impossible : " + ex.Message; }
            }

            return "Outil inconnu.";
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
                text.AppendLine("Ne cherche sur le web que pour un pilote qui pose vraiment question (au plus trois recherches).");
                text.AppendLine("Pour installer des pilotes, préfère DRIVERS-WINDOWS-UPDATE (avec filter) ; pour un périphérique en erreur,");
                text.AppendLine("DEVICE-REINSTALL puis DEVICE-RESCAN ; pour un périphérique désactivé, DEVICE-ENABLE.");
            }
            else
            {
                text.AppendLine("Dépanne le problème décrit par le technicien, étape par étape.");
                text.AppendLine("Domaine : " + (string.IsNullOrWhiteSpace(request.Problem) ? "non précisé" : request.Problem));
                if (request.Details.Count > 0) text.AppendLine("Observations cochées : " + string.Join(" ; ", request.Details));
            }

            if (!string.IsNullOrWhiteSpace(request.Notes))
                text.AppendLine("Constat du technicien : « " + request.Notes!.Trim() + " »");

            text.AppendLine("La fiche de la machine suit. Si elle suffit, rends directement ta première étape avec submit_step ;");
            text.AppendLine("sinon lis seulement ce qui manque.");
            return text.ToString();
        }

        private static string SystemPrompt(IReadOnlyList<AvailableAction> actions)
        {
            var text = new StringBuilder();
            text.AppendLine("Tu es l'assistant de dépannage de LDI12 Diagnostic, le logiciel d'un dépanneur informatique.");
            text.AppendLine("Tu travailles en arrière-plan : personne ne lit de prose. Tout ce que tu rends passe par submit_step,");
            text.AppendLine("que le logiciel transforme en cartes et en boutons. Le technicien agit, répond d'un clic, et le logiciel");
            text.AppendLine("te renvoie ses résultats comme résultat de submit_step : tu proposes alors l'étape suivante.");
            text.AppendLine();
            text.AppendLine("Tes sources : la fiche de la machine jointe à la demande ; read_machine_data pour le détail d'une section");
            text.AppendLine("du diagnostic ; read_live pour l'état à l'instant (son, réseau, un périphérique, un service), utile pour");
            text.AppendLine("constater l'effet d'une opération ; la recherche web avec parcimonie. N'invente aucune donnée.");
            text.AppendLine("Sois économe : chaque lecture est renvoyée à tous les tours suivants. Ne relis pas ce que tu sais déjà.");
            text.AppendLine();
            text.AppendLine("Vise une seule étape. Si la cause est claire, propose d'emblée toutes les opérations qui la règlent,");
            text.AppendLine("avec au plus une vérification finale, et status « resolu » si elles suffisent selon toi. N'étale le dépannage");
            text.AppendLine("en plusieurs étapes que si la cause est vraiment incertaine : chaque étape coûte du temps au technicien.");
            text.AppendLine();
            text.AppendLine("Une bonne étape :");
            text.AppendLine("- Des hypothèses classées, avec une vraisemblance de 0 à 100, mises à jour à chaque étape (écartée, confirmée).");
            text.AppendLine("- Peu d'actions à la fois (une à quatre), les plus probables et les moins risquées d'abord, pour savoir ce qui a agi.");
            text.AppendLine("- Pour tout ce que le logiciel ne peut pas voir (son entendu ou non, voyant allumé, câble, autre appareil,");
            text.AppendLine("  écran, bruit), une vérification sous forme de question fermée avec deux à quatre réponses courtes.");
            text.AppendLine("  Ne pose jamais de question ouverte. Après une opération dont seul l'humain peut juger l'effet,");
            text.AppendLine("  ajoute la vérification qui le dit (par exemple après AUDIO-TEST-SOUND : « Avez-vous entendu la note ? »).");
            text.AppendLine("- status « resolu » quand les résultats montrent que c'est réglé ; « hors_portee » quand il faut une pièce,");
            text.AppendLine("  l'atelier ou le constructeur (dis lequel) ; sinon « en_cours ».");
            text.AppendLine("- En français, phrases courtes et concrètes, pour un technicien.");
            text.AppendLine("- Une action automatique désigne une opération de LDI12 par son identifiant exact, pris dans la liste,");
            text.AppendLine("  avec ses paramètres (device = l'« id » d'un périphérique, endpoint = l'identifiant d'une sortie audio).");
            text.AppendLine("- Ce qu'aucune opération ne fait devient un geste manuel : action_id vide, et dans « manual » le geste précis.");
            text.AppendLine("  Pour un téléchargement, le site officiel du constructeur trouvé par ta recherche, jamais une adresse supposée.");
            text.AppendLine("- Ne propose une réinstallation de Windows que si les étapes l'imposent clairement, et dis pourquoi.");
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
            Description = "Lit une section du relevé que LDI12 a fait de la machine. "
                          + "Pour les listes (devices, drivers, services, startup), « query » garde les seuls éléments qui contiennent ce texte.",
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

        private static BetaTool LiveTool() => new()
        {
            Name = "read_live",
            Description = "Relit l'état de la machine à l'instant, après une opération par exemple. "
                          + "audio : sorties et entrées audio (défaut, volume, coupure, état) ; network : cartes réseau, adresses, passerelle, DNS, "
                          + "et un test de connexion ; device : un périphérique (target = son id) ; service : un service (target = nom court).",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["what"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "audio", "network", "device", "service" } }),
                    ["target"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Pour device : l'id ; pour service : le nom court." }),
                },
                Required = new List<string> { "what" },
            },
        };

        private static BetaTool StepTool() => new()
        {
            Name = "submit_step",
            Description = "Rend une étape du dépannage au technicien. Le résultat de cet outil sera le compte rendu de ce qu'il a fait et constaté.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["status"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "en_cours", "resolu", "hors_portee" } }),
                    ["summary"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "Deux phrases au plus : ce qui est compris à ce point, et ce que cette étape va établir ou régler.",
                    }),
                    ["confidence"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "faible", "moyenne", "forte" } }),
                    ["hypotheses"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                cause = new { type = "string" },
                                likelihood = new { type = "integer", minimum = 0, maximum = 100 },
                                state = new { type = "string", @enum = new[] { "à vérifier", "écartée", "confirmée" } },
                            },
                            required = new[] { "cause", "likelihood", "state" },
                        },
                    }),
                    ["findings"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        description = "Faits relevés qui comptent (version, date, code d'erreur, état).",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string" },
                                detail = new { type = "string" },
                                severity = new { type = "string", @enum = new[] { "info", "attention", "problème", "critique" } },
                            },
                            required = new[] { "title", "detail", "severity" },
                        },
                    }),
                    ["steps"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        description = "Opérations de LDI12 à lancer maintenant, et gestes manuels.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string", description = "Le geste, en quelques mots." },
                                why = new { type = "string", description = "Ce qu'il établit ou règle, en une phrase." },
                                action_id = new { type = "string", description = "Identifiant exact d'une opération de LDI12, ou chaîne vide pour un geste manuel." },
                                parameters = new
                                {
                                    type = "object",
                                    description = "Paramètres de l'opération, s'il en faut (par exemple {\"endpoint\": \"{0.0.0.00000000}.{…}\"}).",
                                    additionalProperties = new { type = "string" },
                                },
                                manual = new { type = "string", description = "Pour un geste manuel : le geste précis." },
                            },
                            required = new[] { "title", "why", "action_id" },
                        },
                    }),
                    ["checks"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array",
                        description = "Vérifications que seul le technicien peut faire, chacune avec ses réponses à un clic.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                question = new { type = "string" },
                                answers = new { type = "array", items = new { type = "string" }, minItems = 2, maxItems = 4 },
                            },
                            required = new[] { "question", "answers" },
                        },
                    }),
                    ["next"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Ce qui se passera ensuite, en une phrase." }),
                },
                Required = new List<string> { "status", "summary", "confidence", "hypotheses", "steps" },
            },
        };

        /// <summary>L'étape telle que Claude l'a rendue ; une opération inconnue devient un geste manuel, jamais une exécution.</summary>
        internal static AssistantPlan ParseStep(IReadOnlyDictionary<string, JsonElement> input, ISet<string> known)
        {
            string Text(JsonElement element, string name)
                => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            string Top(string name, string fallback)
                => input.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

            IEnumerable<JsonElement> Array(string name)
                => input.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

            var hypotheses = Array("hypotheses").Select(item => new Hypothesis
            {
                Cause = Text(item, "cause"),
                Likelihood = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("likelihood", out var l) && l.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(Math.Max(0, Math.Min(100, l.GetDouble())))
                    : 0,
                State = Text(item, "state") is { Length: > 0 } state ? state : "à vérifier",
            }).OrderByDescending(h => h.State == "confirmée").ThenByDescending(h => h.Likelihood).ToList();

            var findings = Array("findings")
                .Select(item => new PlanFinding { Title = Text(item, "title"), Detail = Text(item, "detail"), Severity = Text(item, "severity") })
                .ToList();

            var checks = Array("checks").Select(item => new CheckQuestion
            {
                Question = Text(item, "question"),
                Answers = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("answers", out var answers) && answers.ValueKind == JsonValueKind.Array
                    ? answers.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString() ?? string.Empty)
                        .Where(a => a.Length > 0).Take(4).ToList()
                    : new List<string>(),
            }).Where(c => c.Question.Length > 0 && c.Answers.Count >= 2).ToList();

            var steps = new List<PlanStep>();
            foreach (var item in Array("steps"))
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

            var status = Top("status", "en_cours");
            return new AssistantPlan
            {
                Status = status == "resolu" || status == "hors_portee" ? status : "en_cours",
                Summary = Top("summary", string.Empty),
                Confidence = Top("confidence", "moyenne"),
                Hypotheses = hypotheses,
                Findings = findings,
                Steps = steps,
                Checks = checks,
                Next = Top("next", string.Empty),
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

        private static string LiveLabel(string what) => what switch
        {
            "audio" => "le son",
            "network" => "le réseau",
            "device" => "un périphérique",
            "service" => "un service",
            _ => what,
        };

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
