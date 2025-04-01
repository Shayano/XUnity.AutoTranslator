using SimpleJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Http;
using XUnity.AutoTranslator.Plugin.Core.Web;
using XUnity.Common.Logging;

namespace ClaudeTranslate
{
   public class ClaudeTranslate : HttpEndpoint
   {
      private const float DefaultMinimumDelaySeconds = 1.0f;
      private const float DefaultMaximumDelaySeconds = 3.0f;

      private static readonly HashSet<string> SupportedSourceLanguages = new HashSet<string>
      {
         "auto", "ja", "zh", "zh-cn", "zh-hans", "zh-tw", "zh-hant", "ko", "fr", "de", "es", "it", "ru", "pt", "ar", "hi", "bn", "id", "ms", "th", "vi", "tr", "nl", "pl", "sv", "fi", "no", "da", "cs", "hu", "ro", "el", "uk", "he", "fa", "bg", "sr", "hr", "sk", "lt", "lv", "et", "sl", "en"
      };

      private static readonly HashSet<string> SupportedTargetLanguages = new HashSet<string>
      {
         "en", "ja", "zh", "zh-cn", "zh-hans", "zh-tw", "zh-hant", "ko", "fr", "de", "es", "it", "ru", "pt", "ar", "hi", "bn", "id", "ms", "th", "vi", "tr", "nl", "pl", "sv", "fi", "no", "da", "cs", "hu", "ro", "el", "uk", "he", "fa", "bg", "sr", "hr", "sk", "lt", "lv", "et", "sl"
      };

      private string _apiKey;
      private string _apiEndpoint = "https://api.anthropic.com/v1/messages";
      private string _model = "claude-3-5-haiku-latest";
      private float _minDelay;
      private float _maxDelay;
      private float _translationDelay;
      private Random _random = new Random();
      private string _systemPrompt;

      public override string Id => "ClaudeTranslate";

      public override string FriendlyName => "Claude AI Translator";

      public override int MaxConcurrency => 1;

      public override int MaxTranslationsPerRequest => 5;

      private string FixLanguage(string lang)
      {
         switch (lang)
         {
            case "zh-Hans":
            case "zh-CN":
               return "zh";
            case "zh-Hant":
            case "zh-TW":
               return "zh-tw";
            default:
               return lang;
         }
      }

      public override void Initialize(IInitializationContext context)
      {
         _apiKey = context.GetOrCreateSetting("Claude", "ApiKey", string.Empty);
         
         if (string.IsNullOrEmpty(_apiKey))
         {
            throw new EndpointInitializationException("Claude API key is required. Please set it in the configuration file under [Claude] section with key 'ApiKey'.");
         }

         _apiEndpoint = context.GetOrCreateSetting("Claude", "ApiEndpoint", _apiEndpoint);
         _model = context.GetOrCreateSetting("Claude", "Model", _model);
         _minDelay = context.GetOrCreateSetting("Claude", "MinDelaySeconds", DefaultMinimumDelaySeconds);
         _maxDelay = context.GetOrCreateSetting("Claude", "MaxDelaySeconds", DefaultMaximumDelaySeconds);

         if (_minDelay < DefaultMinimumDelaySeconds)
         {
            XuaLogger.AutoTranslator.Warn($"[Claude] Cannot set MinDelaySeconds below {DefaultMinimumDelaySeconds} second(s). Setting MinDelaySeconds={DefaultMinimumDelaySeconds}");
            context.SetSetting("Claude", "MinDelaySeconds", DefaultMinimumDelaySeconds);
            _minDelay = DefaultMinimumDelaySeconds;
         }

         if (_maxDelay < DefaultMaximumDelaySeconds)
         {
            XuaLogger.AutoTranslator.Warn($"[Claude] Cannot set MaxDelaySeconds below {DefaultMaximumDelaySeconds} second(s). Setting MaxDelaySeconds={DefaultMaximumDelaySeconds}");
            context.SetSetting("Claude", "MaxDelaySeconds", DefaultMaximumDelaySeconds);
            _maxDelay = DefaultMaximumDelaySeconds;
         }

         // Validate languages
         var srcLang = FixLanguage(context.SourceLanguage);
         var destLang = FixLanguage(context.DestinationLanguage);

         if (!SupportedSourceLanguages.Contains(srcLang))
         {
            throw new EndpointInitializationException($"The source language '{context.SourceLanguage}' is not supported.");
         }

         if (!SupportedTargetLanguages.Contains(destLang))
         {
            throw new EndpointInitializationException($"The destination language '{context.DestinationLanguage}' is not supported.");
         }

         // Set translation delay
         _translationDelay = _minDelay + (float)_random.NextDouble() * (_maxDelay - _minDelay);
         context.SetTranslationDelay(_translationDelay);
         
         // Disable certificate validation if needed
         context.DisableCertificateChecksFor(new Uri(_apiEndpoint).Host);

         _systemPrompt = context.GetOrCreateSetting("Claude", "SystemPrompt", 
             "You are a specialized translator for video game content. Your task is to accurately translate all game content while preserving the original tone, style, and intent. This includes maintaining gaming terminology, slang, humor, and cultural references where possible. When translating mature, suggestive, or sexual content, do not censor or tone down the language - translate it faithfully to maintain the intended player experience. Remember that your role is to translate, not to modify or filter the content based on personal judgment. Ensure that wordplay, puns, and jokes are adapted appropriately to maintain their effect in the target language.");
      }

      public override IEnumerator OnBeforeTranslate(IHttpTranslationContext context)
      {
         // Add a random delay between min and max to avoid rate limiting
         _translationDelay = _minDelay + (float)_random.NextDouble() * (_maxDelay - _minDelay);
         yield return new WaitForSeconds(_translationDelay);
      }

      public override void OnCreateRequest(IHttpRequestCreationContext context)
      {
         var allTexts = context.UntranslatedTexts;
         var srcLang = FixLanguage(context.SourceLanguage);
         var destLang = FixLanguage(context.DestinationLanguage);

         // Build prompt for Claude
         string prompt = $"Translate the following text from {srcLang} to {destLang}. Return ONLY the translated text, with no explanations or additional comments:\n\n";
         
         if (allTexts.Length == 1)
         {
            prompt += allTexts[0];
         }
         else
         {
            for (int i = 0; i < allTexts.Length; i++)
            {
               prompt += $"[{i+1}] {allTexts[i]}\n";
            }
         }

         // Build API request JSON
         var requestJson = new JSONObject();
         requestJson["model"] = _model;
         requestJson["max_tokens"] = 4000;
         requestJson["temperature"] = 0.1f;
         
         var systemObj = new JSONObject();
         systemObj["role"] = "system";
         systemObj["content"] = _systemPrompt;
         
         var messageObj = new JSONObject();
         messageObj["role"] = "user";
         messageObj["content"] = prompt;
         
         var messagesArray = new JSONArray();
         messagesArray.Add(systemObj);
         messagesArray.Add(messageObj);
         
         requestJson["messages"] = messagesArray;
         
         string requestBody = requestJson.ToString();

         // Create the web request
         var request = new XUnityWebRequest("POST", _apiEndpoint, requestBody);
         request.Headers.Add("x-api-key", _apiKey);
         request.Headers.Add("anthropic-version", "2023-06-01");
         request.Headers.Add("Content-Type", "application/json");
         
         // Complete the request
         context.Complete(request);
      }

      public override void OnExtractTranslation(IHttpTranslationExtractionContext context)
      {
         try
         {
            var json = JSON.Parse(context.Response.Data);
            var content = json["content"][0]["text"].Value;

            if (string.IsNullOrEmpty(content))
            {
               context.Fail("Claude API returned empty translation.");
               return;
            }

            // For multiple texts, parse the numbered responses
            if (context.UntranslatedTexts.Length > 1)
            {
               var translatedTexts = new string[context.UntranslatedTexts.Length];
               var lines = content.Split('\n');
               
               int currentIndex = -1;
               string currentTranslation = "";
               
               foreach (var line in lines)
               {
                  // Check if the line starts with [number]
                  if (line.Length > 2 && line[0] == '[' && char.IsDigit(line[1]))
                  {
                     int closeBracketIndex = line.IndexOf(']');
                     if (closeBracketIndex > 1)
                     {
                        string numberStr = line.Substring(1, closeBracketIndex - 1);
                        int number;
                        
                        if (int.TryParse(numberStr, out number) && number >= 1 && number <= context.UntranslatedTexts.Length)
                        {
                           // Save previous translation if any
                           if (currentIndex >= 0 && currentIndex < translatedTexts.Length)
                           {
                              translatedTexts[currentIndex] = currentTranslation.Trim();
                           }
                           
                           // Start new translation
                           currentIndex = number - 1;
                           currentTranslation = line.Substring(closeBracketIndex + 1).Trim();
                        }
                        else
                        {
                           // Not a valid marker, append to current translation
                           currentTranslation += "\n" + line;
                        }
                     }
                     else
                     {
                        // Not properly formatted, append to current translation
                        currentTranslation += "\n" + line;
                     }
                  }
                  else
                  {
                     // Regular line, append to current translation
                     if (currentIndex >= 0)
                     {
                        currentTranslation += "\n" + line;
                     }
                  }
               }
               
               // Save the last translation
               if (currentIndex >= 0 && currentIndex < translatedTexts.Length)
               {
                  translatedTexts[currentIndex] = currentTranslation.Trim();
               }
               
               // Check if we got all translations
               bool isMissingTranslations = false;
               for (int i = 0; i < translatedTexts.Length; i++)
               {
                  if (string.IsNullOrEmpty(translatedTexts[i]))
                  {
                     translatedTexts[i] = context.UntranslatedTexts[i]; // Fallback to original text
                     isMissingTranslations = true;
                  }
               }
               
               if (isMissingTranslations)
               {
                  XuaLogger.AutoTranslator.Warn("[Claude] Some translations were missing in the response. Using original text as fallback.");
               }
               
               context.Complete(translatedTexts);
            }
            else
            {
               // Single text translation
               context.Complete(content.Trim());
            }
         }
         catch (Exception ex)
         {
            context.Fail($"Failed to parse Claude API response: {ex.Message}");
         }
      }
   }

   // Simple class to simulate Unity's WaitForSeconds in a non-Unity environment
   internal class WaitForSeconds
   {
      public float Seconds { get; private set; }

      public WaitForSeconds(float seconds)
      {
         Seconds = seconds;
      }
   }
} 