using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using StrmAssistant.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Api
{
    public sealed class ChineseSearchDiagnosticResult
    {
        public bool Success { get; set; }
        public bool Enabled { get; set; }
        public bool SimplifiedTraditionalEnabled { get; set; }
        public bool RuntimePatchActive { get; set; }
        public string RuntimeTarget { get; set; }
        public string Input { get; set; }
        public List<string> Variants { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/Diagnostics/ChineseSearch", "GET",
        Summary = "Preview simplified/traditional search variants without querying the library database")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetChineseSearchDiagnostic : IReturn<ChineseSearchDiagnosticResult>
    {
        public string Query { get; set; }
    }

    public sealed class ChineseSearchDiagnosticsApiService : BaseApiService
    {
        public object Get(GetChineseSearchDiagnostic request)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.GeneralOptions;
            var state = RuntimeModState.Status;
            var result = new ChineseSearchDiagnosticResult
            {
                Enabled = options?.EnableChineseSearchEnhance == true,
                SimplifiedTraditionalEnabled = options?.EnableSimplifiedTraditionalSearch == true,
                RuntimePatchActive = state?.CreateSearchTermPatched == true,
                RuntimeTarget = state?.CreateSearchTermTarget,
                Input = request?.Query
            };

            if (string.IsNullOrWhiteSpace(request?.Query))
            {
                result.Error = "Query is empty.";
                return result;
            }

            try
            {
                var variants = new HashSet<string>(StringComparer.Ordinal) { request.Query };
                AddVariant(variants, request.Query, ChineseConversionDirection.TraditionalToSimplified);
                AddVariant(variants, request.Query, ChineseConversionDirection.SimplifiedToTraditional);
                result.Variants = variants.ToList();
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private static void AddVariant(ISet<string> variants, string input, ChineseConversionDirection direction)
        {
            var converted = ChineseConverter.Convert(input, direction);
            if (!string.IsNullOrWhiteSpace(converted)) variants.Add(converted);
        }
    }
}
