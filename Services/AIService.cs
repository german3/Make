using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;

namespace AppN8N.Services
{
    public class AIService
    {
        private readonly HttpClient _httpClient;
        private readonly string _openRouterApiKey;

        public event Action? OnStateChanged;

        public bool IsGenerating { get; private set; }
        public bool IsVerifyingConnection { get; private set; }
        public string? ConnectionStatus { get; private set; }
        public bool IsExporting { get; private set; }
        public string? ExportError { get; private set; }
        public string? ExportSuccessUrl { get; private set; }
        public List<string> ExecutionLogs { get; private set; } = new();
        public MakeResult? LastResult { get; private set; }

        private readonly string _makeApiToken;
        private readonly string _makeTeamId;
        private readonly string _makeRegion;

        public AIService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _openRouterApiKey = configuration["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("Gemini__ApiKey") ?? "";
            
            _makeApiToken = configuration["Make:ApiToken"] ?? Environment.GetEnvironmentVariable("Make__ApiToken") ?? "";
            _makeTeamId = configuration["Make:TeamId"] ?? Environment.GetEnvironmentVariable("Make__TeamId") ?? "";
            _makeRegion = configuration["Make:Region"] ?? Environment.GetEnvironmentVariable("Make__Region") ?? "us1";
        }

        // ─── GENERAR ESCENARIO MAKE CON IA ──────────────────────────────
        public async Task ProcessPromptAsync(string prompt)
        {
            IsGenerating = true;
            ExportError = null;
            ExportSuccessUrl = null;
            ExecutionLogs.Clear();
            LastResult = null;
            NotifyStateChanged();

            if (string.IsNullOrWhiteSpace(_openRouterApiKey))
            {
                await LogAsync("ERROR: API Key de OpenRouter no configurada o vacía.");
                LastResult = new MakeResult { Description = "Error: Falta API Key.", GeneratedAt = DateTime.Now };
                IsGenerating = false;
                NotifyStateChanged();
                return;
            }

            if (!_openRouterApiKey.StartsWith("sk-or-"))
            {
                await LogAsync("ERROR: El formato de la API Key de OpenRouter es inválido (debe empezar con sk-or-).");
                LastResult = new MakeResult { Description = "Error: Formato de API Key inválido.", GeneratedAt = DateTime.Now };
                IsGenerating = false;
                NotifyStateChanged();
                return;
            }

            await LogAsync("Iniciando análisis de requerimiento...");
            await LogAsync($"Input: '{prompt}'");
            try
            {
                var systemInstruction = @"Eres experto en Make.com. Responde EXCLUSIVAMENTE con un JSON compacto.
¡IMPORTANTE!: RESPETA MAYÚSCULAS/MINÚSCULAS EXACTAMENTE (STRICT CASE).

Mapeo de module (STRICT CASE - ¡NO OLVIDES EL 'Web'!):
- Webhook (Trigger): gateway:CustomWebHook (DEBE llevar 'Web')
- Webhook (Respuesta): gateway:WebhookRespond (h minúscula, termina en Respond)
- HTTP: http:ActionSendData
- GoogleSheets: google-sheets:ActionAddRow
- Slack: slack:ActionPostMessage
- Gmail: gmail:ActionSendEmail
- OpenAI: openai-gpt-3:ActionCreateChatCompletion

Estructura:
{
  ""moduleCount"": 0,
  ""description"": """",
  ""makeModules"": [{ ""id"":1, ""app"":"""", ""appLabel"":"""", ""module"":"""", ""label"":"""", ""color"":"""", ""icon"":"""" }],
  ""makeBlueprintData"": {
    ""name"": """",
    ""flow"": [{ ""id"":1, ""module"":""USAR_MAPEO_EXACTO"", ""version"":1, ""parameters"":{}, ""mapper"":{}, ""metadata"":{""designer"":{""x"":0,""y"":0,""name"":""""}} }],
    ""metadata"": { ""instant"":false, ""version"":1, ""scenario"":{""roundtrips"":1,""maxErrors"":3,""autoCommit"":true,""autoCommitTriggerLast"":true,""sequential"":false}, ""designer"":{""orphans"":[]}, ""zone"":""us1.make.com"" }
  }
}

Reglas ESTRICTAS:
- No uses markdown (ni ```json ni ```)
- Los IDs deben ser números enteros consecutivos empezando en 1
- Las posiciones x deben ser múltiplos de 300 (0, 300, 600, 900...), y siempre 0
- Conserva MAYÚSCULAS/minúsculas de los módulos exactamente como se indican.";

                var payload = new
                {
                    model = "google/gemini-2.0-flash-001",
                    max_tokens = 3000,
                    messages = new[]
                    {
                        new { role = "system", content = systemInstruction },
                        new { role = "user", content = "Crea: " + prompt }
                    }
                };

                await LogAsync("Enviando petición a OpenRouter / Gemini Flash 2.0...");

                var url = "https://openrouter.ai/api/v1/chat/completions";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openRouterApiKey);
                request.Headers.Add("HTTP-Referer", "http://localhost:7016/");
                request.Headers.Add("X-Title", "AppMake");
                request.Content = JsonContent.Create(payload);

                var response = await _httpClient.SendAsync(request);
                await LogAsync($"Respuesta HTTP: {(int)response.StatusCode} {response.ReasonPhrase}");

                if (response.IsSuccessStatusCode)
                {
                    await LogAsync("Procesando respuesta JSON...");
                    var jsonString = await response.Content.ReadAsStringAsync();

                    var openRouterResponse = JsonSerializer.Deserialize<OpenRouterResponseRoot>(jsonString,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var rawText = openRouterResponse?.Choices?[0]?.Message?.Content ?? "{}";

                    // Log inicial para depuración de estructura
                    await LogAsync($"Longitud recibida: {rawText.Length} caracteres.");

                    // Limpiar markdown residual
                    rawText = rawText.Replace("```json", "").Replace("```", "").Trim();

                    // Intentar extraer sólo el JSON si hay texto extra antes o después
                    var jsonStart = rawText.IndexOf('{');
                    var jsonEnd = rawText.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                        rawText = rawText[jsonStart..(jsonEnd + 1)];

                    var options = new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    };
                    var parsedData = JsonSerializer.Deserialize<GeminiMakeResult>(rawText, options);

                    // Guardar el blueprint completo para descargar
                    JsonElement? blueprintData = null;
                    using var doc = JsonDocument.Parse(rawText);
                    if (doc.RootElement.TryGetProperty("makeBlueprintData", out var bpElement))
                        blueprintData = bpElement.Clone();

                    ExecutionLogs.Add("Arquitectura de módulos mapeada. Renderizando canvas Make...");
                    LastResult = new MakeResult
                    {
                        ModuleCount = parsedData?.ModuleCount ?? 0,
                        Description = parsedData?.Description ?? "Escenario generado.",
                        GeneratedAt = DateTime.Now,
                        Modules = parsedData?.MakeModules ?? new List<MakeModule>(),
                        BlueprintData = blueprintData
                    };

                    await LogAsync("✔ Escenario generado exitosamente.");
                    await LogAsync("⬇ Descarga el Blueprint para importarlo en Make.com de forma gratuita.");
                }
                else
                {
                    var errorMsg = await response.Content.ReadAsStringAsync();
                    await LogAsync($"ERROR en API: {errorMsg}");
                }
            }
            catch (Exception ex)
            {
                await LogAsync($"Excepción: {ex.Message}");
            }
            finally
            {
                IsGenerating = false;
                NotifyStateChanged();
            }
        }

        // ─── EXPORTAR ESCENARIO A MAKE.COM API V2 ─────────────────────
        public async Task ExportToMakeAsync()
        {
            if (LastResult?.BlueprintData == null) return;

            IsExporting = true;
            ExportError = null;
            ExportSuccessUrl = null;
            NotifyStateChanged();

            try
            {
                if (string.IsNullOrWhiteSpace(_makeApiToken) || _makeApiToken == "TU_API_TOKEN_AQUI")
                    throw new Exception("Falta configurar el Token de Make en appsettings.json");

                await LogAsync("Iniciando exportación a Make.com API...");

                var url = $"https://{_makeRegion}.make.com/api/v2/scenarios";
                var blueprint = LastResult.BlueprintData.Value;
                var scenarioName = blueprint.TryGetProperty("name", out var n) ? n.GetString() : "Escenario IA";

                var payload = new
                {
                    name = scenarioName,
                    teamId = int.TryParse(_makeTeamId, out int tid) ? tid : 0,
                    blueprint = blueprint.GetRawText(),
                    scheduling = "{\"type\":\"on-demand\"}"
                };

                if (payload.teamId == 0) throw new Exception("TeamId inválido en appsettings.json");

                var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
                await LogAsync($"Enviando payload (Length: {payloadJson.Length})...");
                
                // Loguear los módulos generados para depuración
                if (blueprint.TryGetProperty("flow", out var flowArray)) {
                    foreach (var m in flowArray.EnumerateArray()) {
                        var mId = m.TryGetProperty("module", out var mod) ? mod.GetString() : "N/A";
                        await LogAsync($"Módulo detectado en JSON: {mId}");
                    }
                }

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", $"Token {_makeApiToken}");
                request.Content = JsonContent.Create(payload);

                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var resDoc = JsonDocument.Parse(responseContent);
                    var scenarioId = resDoc.RootElement.GetProperty("scenario").GetProperty("id").GetInt32();
                    
                    ExportSuccessUrl = $"https://{_makeRegion}.make.com/{_makeTeamId}/scenarios/{scenarioId}/edit";
                    await LogAsync($"🚀 ¡Éxito! Escenario creado en Make.com (ID: {scenarioId})");
                }
                else
                {
                    await LogAsync($"❌ Error API Make: {responseContent}");
                    ExportError = $"Error de API ({response.StatusCode}): {responseContent}";
                    
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) {
                        await LogAsync("⚠️ Sugerencia: Revisa que tu Token tenga el scope 'scenarios:write' y que el TeamId sea el correcto.");
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync($"❌ Error de Exportación: {ex.Message}");
                ExportError = ex.Message;
            }
            finally
            {
                IsExporting = false;
                NotifyStateChanged();
            }
        }

        public async Task VerifyMakeConnectionAsync()
        {
            if (IsVerifyingConnection) return;
            IsVerifyingConnection = true;
            ConnectionStatus = "⌛ Verificando...";
            ExecutionLogs.Clear();
            NotifyStateChanged();

            try
            {
                await LogAsync("Iniciando prueba de conexión con Make.com...");
                var url = $"https://{_makeRegion}.make.com/api/v2/teams";
                
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", $"Token {_makeApiToken}");

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    ConnectionStatus = "✅ Conexión Exitosa";
                    await LogAsync("✅ Conexión establecida correctamente.");
                    await LogAsync($"Equipos encontrados: {content.Length} bytes de info recibida.");
                }
                else
                {
                    ConnectionStatus = $"❌ Error {response.StatusCode}";
                    await LogAsync($"❌ Error al conectar: {content}");
                }
            }
            catch (Exception ex)
            {
                ConnectionStatus = "❌ Error Crítico";
                await LogAsync($"❌ Excepción: {ex.Message}");
            }
            finally
            {
                IsVerifyingConnection = false;
                NotifyStateChanged();
            }
        }

        private async Task LogAsync(string message)
        {
            ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            NotifyStateChanged();
            await Task.Delay(150);
        }

        private void NotifyStateChanged() => OnStateChanged?.Invoke();
    }

    // ─── MODELOS DE DATOS ────────────────────────────────────────────────
    public class MakeResult
    {
        public int ModuleCount { get; set; }
        public string? Description { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<MakeModule> Modules { get; set; } = new();
        public JsonElement? BlueprintData { get; set; }
    }

    public class GeminiMakeResult
    {
        public int ModuleCount { get; set; }
        public string? Description { get; set; }
        public List<MakeModule>? MakeModules { get; set; }
    }

    public class MakeModule
    {
        public int Id { get; set; }
        public string App { get; set; } = "";
        public string AppLabel { get; set; } = "";
        public string Module { get; set; } = "action";
        public string Label { get; set; } = "";
        public string Color { get; set; } = "#FF6B6B";
        public string Icon { get; set; } = "🔧";
    }

    // ─── DESERIALIZACIÓN OPENROUTER ─────────────────────────────────────
    public class OpenRouterResponseRoot
    {
        public List<OpenRouterChoice>? Choices { get; set; }
    }
    public class OpenRouterChoice
    {
        public OpenRouterMessage? Message { get; set; }
    }
    public class OpenRouterMessage
    {
        public string? Content { get; set; }
    }
}
