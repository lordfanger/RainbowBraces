﻿using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace RainbowBraces
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("code")]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    [TagType(typeof(IClassificationTag))]
    public class CreationListener : IViewTaggerProvider
    {
        [Import]
        internal IClassificationTypeRegistryService _registry = null;

        [Import]
        internal IViewTagAggregatorFactoryService _aggregator = null;

        private static readonly List<string> _unsupportedContentTypes = new() { "HTML", "HTMLX", "html-delegation", "WebForms", "Razor", "LegacyRazorCSharp" };

        public bool _isProcessing { get; set; }
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            //We can ignore anything HTML, embedded languages will be handled separately
            if (_unsupportedContentTypes.Any(ct => buffer.ContentType.IsOfType(ct)))
            {
                return null;
            }

            // Calling CreateTagAggregator creates a recursive situation, so _isProcessing ensures it only runs once per textview.
            if (!_isProcessing)
            {
                _isProcessing = true;
                ITagAggregator<IClassificationTag> aggregator = _aggregator.CreateTagAggregator<IClassificationTag>(textView);
                _isProcessing = false;

                return buffer.Properties.GetOrCreateSingletonProperty(() => new RainbowTagger(textView, buffer, _registry, aggregator)) as ITagger<T>;
            }

            return null;
        }
    }
}