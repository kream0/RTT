using System;

namespace RepoToTxtGui
{
    public enum LlmModel
    {
        Gpt4o,        // OpenAI GPT-4o
        Gpt35Turbo,   // OpenAI GPT-3.5 Turbo
        ClaudeSonnet, // Anthropic Claude Sonnet (using proxy tokenizer)
        GeminiPro     // Google Gemini Pro (using proxy tokenizer)
    }
}