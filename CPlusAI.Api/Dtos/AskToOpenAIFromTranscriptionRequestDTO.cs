namespace CPlusAI.Api.Dtos
{
    public class AskToOpenAIFromTranscriptionRequestDTO
    {
        public required string Question { get; set; }
        public required string Transcription { get; set; }
    }
}