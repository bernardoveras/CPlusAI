using System;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CPlusAI.Api.Dtos;
using Microsoft.AspNetCore.Mvc;
using OpenAI_API;
using OpenAI_API.Completions;
using OpenAI_API.Models;

namespace CPlusAI.Api.Controllers;

[ApiController]
[Route("transcript", Name = "Transcript Audio")]
public class TranscriptController : ControllerBase
{
    private readonly ILogger<TranscriptController> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public TranscriptController(
        ILogger<TranscriptController> logger,
        IConfiguration configuration
    )
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = new HttpClient();

        var assemblyAiApiKey = _configuration.GetValue<string>("AssemblyAI:API_KEY");
        var assemblyAiBaseUrl = _configuration.GetValue<string>("AssemblyAI:BASE_URL");

        if (assemblyAiApiKey != null && assemblyAiBaseUrl != null)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(assemblyAiApiKey);
            _httpClient.BaseAddress = new Uri(assemblyAiBaseUrl);
        }
    }

    [HttpPost("upload", Name = "UploadFile")]
    public async Task<ActionResult<string>> UploadFileAsync([FromQuery] string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return BadRequest("Informe o caminho do arquivo.");

            using var fileStream = System.IO.File.OpenRead(filePath);
            using var fileContent = new StreamContent(fileStream);

            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await _httpClient.PostAsync("upload", fileContent);

            response.EnsureSuccessStatusCode();

            var jsonDoc = await response.Content.ReadFromJsonAsync<JsonDocument>();

            if (jsonDoc == null)
                return BadRequest("Não foi possível ler o arquivo recebido.");

            return Ok(jsonDoc.RootElement.GetProperty("upload_url").GetString());
        }
        catch (Exception ex)
        {
            return BadRequest(ex.ToString());
        }
    }

    [HttpGet(Name = "Transcript")]
    public async Task<ActionResult<TranscriptDTO>> TranscriptAsync([FromQuery] string audioUrl)
    {
        try
        {
            var data = new {
                audio_url = audioUrl,
                language_code = "pt"
            };

            var content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");


            using var response = await _httpClient.PostAsync("transcript", content);

            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadFromJsonAsync<JsonDocument>();

            if (responseJson == null)
                return BadRequest("Não foi possível ler o arquivo recebido.");


            var transcriptId = responseJson.RootElement.GetProperty("id").GetString();

            if (transcriptId == null)
                return BadRequest("Não foi possível encontrar o id da transcrição");

            return await WaitForTranscriptToProcess(transcriptId);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.ToString());
        }
    }

    private async Task<ActionResult<TranscriptDTO>> WaitForTranscriptToProcess(string transcriptId)
    {
        while (true)
        {
            using var pollingResponse = await _httpClient.GetAsync($"transcript/{transcriptId}");

            pollingResponse.EnsureSuccessStatusCode();

            var transcriptReceived = await pollingResponse.Content.ReadFromJsonAsync<TranscriptDTO>();

            if (transcriptReceived == null)
                return BadRequest("Não foi possível ler a transcrição recebida.");

            switch (transcriptReceived.Status)
            {
                case "processing":
                case "queued":
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    break;
                case "completed":
                    return Ok(transcriptReceived!);
                case "error":
                    return StatusCode(500, $"Transcription failed: {transcriptReceived.Error}");
                default:
                    return BadRequest("This code shouldn't be reachable.");
            }
        }
    }

    [HttpPost("ai/generate/description")]
    public async Task<IActionResult> GenerateDescriptionWithOpenAI([FromBody] GenerateDescriptionWithOpenAIRequestDTO request)
    {
        try
        {
            var openAiApiKey = _configuration.GetValue<string>("OpenAI:API_KEY");

            var openAi = new OpenAIAPI(openAiApiKey);

            var chat = openAi.Chat.CreateConversation();
            chat.Model = Model.GPT4_Turbo;
            chat.RequestParameters.Temperature = 0;

            chat.AppendSystemMessage("Se comporte como um especialista em sistemas que cria resumos a partir da transcrição de uma aula. Os resumos devem ter foco educacional para dar contexto sobre uma aula que será publicada numa plataforma de vídeos e deve explicar detalhes sobre o vídeo sem necessariamente replicar o passo à passo do vídeo.");
            chat.AppendSystemMessage("Responda em primeira pessoa como se você fosse o instrutor da aula. Utilize uma linguagem menos formal. Evite repetir as palavras muitas vezes, use sinônimos sempre que possível.");
            chat.AppendSystemMessage("Seja sucinto e retorne no máximo 80 palavras em markdown sem cabeçalhos.");

            chat.AppendUserInput($"Gere um resumo da transcrição abaixo. Retorne o resumo no mesmo idioma da transcrição. \n\n{request.Transcription}");
            
            var outputResult = await chat.GetResponseFromChatbotAsync();

            return Ok(outputResult);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.ToString());
        }
    }

    [HttpPost("ai/ask")]
    public async Task<IActionResult> AskToOpenAI([FromBody] AskToOpenAIFromTranscriptionRequestDTO request)
    {
        try
        {
            var openAiApiKey = _configuration.GetValue<string>("OpenAI:API_KEY");

            var openAi = new OpenAIAPI(openAiApiKey);

            var chat = openAi.Chat.CreateConversation();
            chat.Model = Model.GPT4_Turbo;
            chat.RequestParameters.Temperature = 0;

            chat.AppendSystemMessage("Se comporte como um especialista em sistemas e com base na transcrição tire as dúvidas.");
            chat.AppendSystemMessage("Responda de forma clara e objetiva para resolver o problema do usuário.");
            chat.AppendSystemMessage("Não fale que é um video tutorial e nem que as respostas são embasadas de uma transcrição. Tem que ser o mais normal possível.");
            chat.AppendSystemMessage("Caso não tenha certeza da sua resposta, informe que vai encaminhar a um especialista.");
            chat.AppendSystemMessage($"Responda com base na transcrição abaixo com muita atenção.\n\n {request.Transcription}");

            chat.AppendUserInput($"Gere a resposta da pergunta abaixo.. \n\n{request.Question}");

            var outputResult = await chat.GetResponseFromChatbotAsync();

            return Ok(outputResult);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.ToString());
        }
    }
}

