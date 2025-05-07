using SharpToken; // Add using for SharpToken
using System;
using System.Collections.Generic;

namespace RepoToTxtGui
{
    public class TokenCounterService
    {
        private readonly GptEncoding _cl100kBaseEncoding;
        private readonly GptEncoding _o200kBaseEncoding; // For GPT-4o models
        private readonly bool _o200kAvailable;

        public TokenCounterService()
        {
            // Initialize encodings with error handling
            try
            {
                _cl100kBaseEncoding = GptEncoding.GetEncoding("cl100k_base"); // Used by GPT-3.5 and GPT-4
                Console.WriteLine("DEBUG: cl100k_base encoding loaded successfully");

                try
                {
                    _o200kBaseEncoding = GptEncoding.GetEncoding("o200k_base");  // Used by GPT-4o
                    _o200kAvailable = true;
                    Console.WriteLine("DEBUG: o200k_base encoding loaded successfully");

                    // Test the encoder with a simple string to verify it works
                    try
                    {
                        var testTokens = _o200kBaseEncoding.Encode("This is a test");
                        Console.WriteLine($"DEBUG: o200k_base test encoding successful - token count: {testTokens.Count}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DEBUG: o200k_base encoding test failed: {ex.Message}");
                        _o200kAvailable = false;
                        _o200kBaseEncoding = _cl100kBaseEncoding; // Fall back if test encoding fails
                    }
                }
                catch (Exception ex)
                {
                    // If o200k_base is not available, fall back to cl100k_base
                    Console.WriteLine($"DEBUG: Failed to load o200k_base encoding: {ex.Message}");
                    _o200kBaseEncoding = _cl100kBaseEncoding;
                    _o200kAvailable = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Failed to initialize any tokenizers: {ex.Message}");
                throw new InvalidOperationException($"Failed to initialize tokenizers: {ex.Message}", ex);
            }
        }

        public (int Count, string EncodingName, bool IsProxy) CountTokens(string text, LlmModel model)
        {
            if (string.IsNullOrEmpty(text))
            {
                return (0, GetEncodingNameForModel(model), IsModelUsingProxy(model));
            }

            // Get the appropriate encoding based on the model
            var (encoding, encodingName) = GetEncodingForModel(model);
            bool isProxy = IsModelUsingProxy(model);

            try
            {
                // Specific handling for o200k_base encoding which might have issues
                if (model == LlmModel.Gpt4o && _o200kAvailable)
                {
                    try
                    {
                        List<int> tokens = encoding.Encode(text);
                        int tokenCount = tokens.Count;
                        return (tokenCount, encodingName, isProxy);
                    }
                    catch (Exception)
                    {
                        // If o200k_base encoding fails on this specific text, fall back to cl100k_base
                        List<int> tokens = _cl100kBaseEncoding.Encode(text);
                        int tokenCount = tokens.Count;
                        return (tokenCount, "cl100k_base (o200k fallback)", true);
                    }
                }
                else
                {
                    // Regular handling for other models
                    List<int> tokens = encoding.Encode(text);
                    int tokenCount = tokens.Count;
                    return (tokenCount, encodingName, isProxy);
                }
            }
            catch (Exception)
            {
                // If encoding fails completely, return 0 and indicate failure
                return (0, encodingName, true);
            }
        }

        private (GptEncoding Encoding, string EncodingName) GetEncodingForModel(LlmModel model)
        {
            switch (model)
            {
                case LlmModel.Gpt4o:
                    return _o200kAvailable
                        ? (_o200kBaseEncoding, "o200k_base")
                        : (_cl100kBaseEncoding, "cl100k_base (o200k unavailable)");
                case LlmModel.Gpt35Turbo:
                    return (_cl100kBaseEncoding, "cl100k_base");
                case LlmModel.ClaudeSonnet:
                case LlmModel.GeminiPro:
                    // Use cl100k_base as a proxy for non-OpenAI models
                    return (_cl100kBaseEncoding, "cl100k_base");
                default:
                    return (_cl100kBaseEncoding, "cl100k_base");
            }
        }

        private bool IsModelUsingProxy(LlmModel model)
        {
            switch (model)
            {
                case LlmModel.Gpt4o:
                    return !_o200kAvailable;
                case LlmModel.Gpt35Turbo:
                    return false; // Using native tokenizers
                case LlmModel.ClaudeSonnet:
                case LlmModel.GeminiPro:
                    return true;  // Using cl100k_base as a proxy
                default:
                    return true;  // Default to proxy for unknown models
            }
        }

        private string GetEncodingNameForModel(LlmModel model)
        {
            switch (model)
            {
                case LlmModel.Gpt4o:
                    return _o200kAvailable ? "o200k_base" : "cl100k_base (o200k unavailable)";
                case LlmModel.Gpt35Turbo:
                    return "cl100k_base";
                case LlmModel.ClaudeSonnet:
                case LlmModel.GeminiPro:
                    return "cl100k_base";
                default:
                    return "cl100k_base";
            }
        }
    }
}