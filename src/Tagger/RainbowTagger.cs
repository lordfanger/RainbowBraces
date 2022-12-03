using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;

namespace RainbowBraces
{
    public class RainbowTagger : ITagger<IClassificationTag>
    {
        static RainbowTagger()
        {
            try
            {
                string dir = @"C:\Temp\RainbowBraces";
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                if (System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Break();
                else System.Diagnostics.Debugger.Launch();
                /* ignore */
            }
        }

        private const int _maxLineLength = 100000;
        private const int _overflow = 200;

        private readonly ITextBuffer _buffer;
        private readonly ITextView _view;
        private readonly IClassificationTypeRegistryService _registry;
        private readonly ITagAggregator<IClassificationTag> _aggregator;
        private readonly Debouncer _debouncer;
        private List<ITagSpan<IClassificationTag>> _tags = new();
        private List<BracePair> _braces = new();
        private int _lastTagCount;
        private bool _isRazor;
        private bool _isEnabled;
        private static readonly Regex _regex = new(@"[\{\}\(\)\[\]]", RegexOptions.Compiled);
        private static readonly Span _empty = new(0, 0);


        public RainbowTagger(ITextView view, ITextBuffer buffer, IClassificationTypeRegistryService registry, ITagAggregator<IClassificationTag> aggregator, bool isRazor)
        {
            _buffer = buffer;
            _view = view;
            _registry = registry;
            _aggregator = aggregator;
            _isRazor = isRazor;
            _isEnabled = General.Instance.Enabled;
            _debouncer = new(General.Instance.Timeout);

            _buffer.Changed += OnBufferChanged;
            view.Closed += OnViewClosed;
            view.LayoutChanged += View_LayoutChanged;
            General.Saved += OnSettingsSaved;

            if (_isEnabled)
            {
                ThreadHelper.JoinableTaskFactory.StartOnIdle(async () =>
                {
                    await ParseAsync();
                    HandleRatingPrompt();
                }, VsTaskRunContext.UIThreadIdlePriority).FireAndForget();
            }
        }

        private void View_LayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            (int visibleStart, int visibleEnd) = GetViewportSpanWithOverflow();
            int tagCount = _aggregator.GetTags(new SnapshotSpan(_buffer.CurrentSnapshot, visibleStart, visibleEnd - visibleStart)).Count();

            Debug.WriteLine($"View_LayoutChanged tags:{tagCount}/{_lastTagCount} {e.HorizontalTranslation}/{e.VerticalTranslation}");
            if (_isEnabled && (e.VerticalTranslation || e.HorizontalTranslation || tagCount != _lastTagCount))
            {
                _debouncer.Debouce(() => { _ = ParseAsync(); });
            }
            _lastTagCount = tagCount;
        }

        private void OnSettingsSaved(General settings)
        {
            if (settings.Enabled)
            {
                ParseAsync().FireAndForget();
            }
            else
            {
                _braces.Clear();
                _tags.Clear();
                int visibleStart = _view.TextViewLines.First().Start.Position;
                int visibleEnd = _view.TextViewLines.Last().End.Position;
                TagsChanged?.Invoke(this, new(new(_buffer.CurrentSnapshot, visibleStart, visibleEnd - visibleStart)));
            }

            _isEnabled = settings.Enabled;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            ITextView view = (ITextView)sender;
            view.TextBuffer.Changed -= OnBufferChanged;
            view.Closed -= OnViewClosed;
            General.Saved -= OnSettingsSaved;
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isEnabled && e.Changes.Count > 0)
            {
                int startPosition = e.Changes.Min(change => change.OldPosition);
                _debouncer.Debouce(() => { _ = ParseAsync(startPosition); });
            }
        }

        IEnumerable<ITagSpan<IClassificationTag>> ITagger<IClassificationTag>.GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_tags.Count == 0 || spans.Count == 0 || spans[0].IsEmpty)
            {
                return null;
            }

            return _tags.Where(p => spans[0].IntersectsWith(p.Span.Span));
        }

        public async Task ParseAsync(int topPosition = 0)
        {
            General options = await General.GetLiveInstanceAsync();

            // Must be performed on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_buffer.CurrentSnapshot.LineCount > _maxLineLength)
            {
                await VS.StatusBar.ShowMessageAsync($"No rainbow braces. File too big ({_buffer.CurrentSnapshot.LineCount} lines).");
                return;
            }

            (Span Span, IMappingTagSpan<IClassificationTag> Tag)[] allTags = null;
            string fileName = null;
            try
            {
                if (_buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument) && textDocument is { FilePath: { } filePath })
                {
                    fileName = Path.GetFileName(filePath);
                }
                else
                {
                    fileName = Guid.NewGuid().ToString();
                }
                allTags = _aggregator.GetTags(new SnapshotSpan(_buffer.CurrentSnapshot, 0, _buffer.CurrentSnapshot.Length))
                    .SelectMany(t => t.Span.GetSpans(_buffer).Select(t2 => (Span: t2.Span, Tag: t)))
                    .Reverse()
                    .OrderBy(t => t.Span.Start)
                    .ToArray();
                Debug.WriteLine($"ParseAsync tags:{allTags!.Length}/{_lastTagCount}");
            }
            catch
            {
                if (System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Break();
                else System.Diagnostics.Debugger.Launch();
            }

            int visibleStart = Math.Max(_view.TextViewLines.First().Start.Position - _overflow, 0);
            int visibleEnd = Math.Min(_view.TextViewLines.Last().End.Position + _overflow, _buffer.CurrentSnapshot.Length);
            ITextSnapshotLine changedLine = _buffer.CurrentSnapshot.GetLineFromPosition(topPosition);

            SnapshotSpan wholeDocSpan = new(_buffer.CurrentSnapshot, 0, visibleEnd);
            SnapshotSpan[] disallow = _aggregator.GetTags(wholeDocSpan)
                                                     .Where(t => !IsAllowed(t))
                                                     .SelectMany(d => d.Span.GetSpans(_buffer)).ToArray();

            bool IsAllowed(IMappingTagSpan<IClassificationTag> t)
            {
                IClassificationType type = t.Tag.ClassificationType;
                if (type.IsOfType(PredefinedClassificationTypeNames.Punctuation)) return true;
                if (type.IsOfType(PredefinedClassificationTypeNames.Operator)) return true;
                if (type.IsOfType("XAML Delimiter")) return true;
                if (type.IsOfType("SQL Operator")) return true;
                if (_isRazor)
                {
                    if (type.IsOfType(PredefinedClassificationTypeNames.String)) return true; // for template strings
                    if (type.IsOfType("RazorDirective")) return true;
                }
                return false;
            }

            // TODO wait for init
            if (_isRazor && disallow.Length == 0)
            {

            }

            // Move the rest of the execution to a background thread.
            await TaskScheduler.Default;

            try
            {
                if (allTags is { Length: > 0 })
                {
                    string baseDir = @"C:\Temp\RainbowBraces";
                    Directory.CreateDirectory(baseDir);
                    string versionFileName = $"{fileName}_{_buffer.ContentType.TypeName}_{_buffer.CurrentSnapshot.Version.VersionNumber}";
                    FileInfo fileList = new(Path.Combine(baseDir, $@"{versionFileName}.txt"));
                    {
                        using StreamWriter writer = fileList.CreateText();
                        foreach (string tag in allTags.Select(t => $"{t.Tag.Span} - {t.Tag.Tag.ClassificationType.Classification}"))
                        {
                            await writer.WriteLineAsync(tag);
                        }
                    }

                    FileInfo fileHtml = new(Path.Combine(baseDir, $"{versionFileName}.html"));
                    {
                        using StreamWriter writer = fileHtml.CreateText();
                        string html = _buffer.CurrentSnapshot.GetText();
                        ILookup<int, (Span Span, IMappingTagSpan<IClassificationTag> Tag)> tagStarts = allTags.OrderByDescending(t => t.Span.End).ToLookup(t => t.Span.Start);
                        ILookup<int, (Span Span, IMappingTagSpan<IClassificationTag> Tag)> tagEnds = allTags.ToLookup(t => t.Span.End);
                        HashSet<string> classifications = new();

                        await writer.WriteAsync("<html><body>");
                        for (int i = 0; i < html.Length; i++)
                        {
                            if (tagEnds[i].Any())
                            {
                                for (int j = 0; j < tagEnds[i].GroupBy(t => (t.Span.Start, t.Tag.Tag.ClassificationType.Classification)).Count(); j++)
                                {
                                    await writer.WriteAsync("</span>");
                                }
                            }

                            if (tagStarts[i].Any())
                            {
                                foreach (IGrouping<string, (Span Span, IMappingTagSpan<IClassificationTag> Tag)> tagStart in tagStarts[i].GroupBy(t => t.Tag.Tag.ClassificationType.Classification))
                                {
                                    string normalizedKey = Regex.Replace(tagStart.Key.ToLower().Replace(' ', '_'), @"[^a-z_]", "");
                                    await writer.WriteAsync($"<span class=\"{normalizedKey}\" data-group-count=\"{tagStart.Count()}\" data-span=\"{string.Join(",", tagStart.Select(t => t.Span))}\"><sub>{tagStart.Key}</sub>");
                                    classifications.Add(normalizedKey);
                                }
                            }
                            char c = html[i];
                            if (c is '\n' or '\r') await writer.WriteAsync("<br />");
                            else if (c == '>') await writer.WriteAsync("&gt;");
                            else if (c == '<') await writer.WriteAsync("&lt;");
                            else await writer.WriteAsync(c);
                        }

                        int hue = 0;
                        double saturation = 50;
                        double lightness = 25;

                        await writer.WriteLineAsync("<style>");
                        await writer.WriteLineAsync("html,body { white-space: pre; color: #aaa; background: #222; }");
                        await writer.WriteLineAsync("br + br { display: none; }");
                        await writer.WriteLineAsync("span { position: relative; }");
                        await writer.WriteLineAsync("span > sub { font-size: 0.9em; opacity: 0.85; position: absolute; top: 100%; left: 0; background: black; border: solid 1px; border-radius: 3px; padding: 1px; z-index: 1; }");
                        await writer.WriteLineAsync("span > span > sub { top: auto; bottom: 100%; }");
                        await writer.WriteLineAsync("span:not(:hover) > sub { display: none; }");
                        foreach (string classification in classifications)
                        {
                            hue = (hue + 137) % 360; // 0-360
                            saturation = (saturation + 1.618 * 60) % 60; // 40-100%
                            lightness = (lightness - 1.618 * 30) % 30; // 50-80%

                            await writer.WriteLineAsync($"span.{classification} {{ color: hsl({hue}, {(int)saturation + 40}%, {(int)lightness + 50}%); }}");
                        }

                        await writer.WriteLineAsync("</style>");
                        await writer.WriteLineAsync("</body></head>");
                    }
                }
            }
            catch
            {
                if (System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Break();
                else System.Diagnostics.Debugger.Launch();
            }

            List<BracePair> pairs = new();

            if (changedLine.LineNumber > 0)
            {
                // Use the cache for all brackets defined above the position of the change
                pairs.AddRange(_braces.Where(IsAboveChange));

                foreach (BracePair pair in pairs)
                {
                    pair.Close = pair.Close.End >= visibleStart ? _empty : pair.Close;
                }

                bool IsAboveChange(BracePair p)
                {
                    // empty spans can be ignored especially the [0..0) that would be always above change
                    if (!p.Open.IsEmpty && p.Open.End <= visibleStart) return true;
                    if (!p.Close.IsEmpty && p.Close.End <= visibleStart) return true;
                    return false;
                }
            }

            foreach (ITextSnapshotLine line in _buffer.CurrentSnapshot.Lines)
            {
                if ((changedLine.LineNumber > 0 && (line.End < visibleStart) || line.Extent.IsEmpty))
                {
                    continue;
                }

                string text = line.GetText();
                MatchCollection matches = _regex.Matches(text);

                if (matches.Count == 0)
                {
                    continue;
                }

                foreach (Match match in matches)
                {
                    char c = match.Value[0];
                    int position = line.Start + match.Index;

                    if (disallow.Any(s => s.Start <= position && s.End > position))
                    {
                        continue;
                    }

                    Span braceSpan = new(position, 1);

                    if (options.Parentheses && (c == '(' || c == ')'))
                    {
                        BuildPairs(pairs, c, braceSpan, '(', ')');
                    }
                    else if (options.CurlyBrackets && (c == '{' || c == '}'))
                    {
                        BuildPairs(pairs, c, braceSpan, '{', '}');
                    }
                    else if (options.SquareBrackets && (c == '[' || c == ']'))
                    {
                        BuildPairs(pairs, c, braceSpan, '[', ']');
                    }
                }

                if (line.End >= visibleEnd || line.LineNumber >= _buffer.CurrentSnapshot.LineCount)
                {
                    break;
                }
            }

            _braces = pairs;
            _tags = GenerateTagSpans(pairs, options.CycleLength);
            TagsChanged?.Invoke(this, new(new(_buffer.CurrentSnapshot, visibleStart, visibleEnd - visibleStart)));
        }

        private (int VisibleStart, int VisibleEnd) GetViewportSpanWithOverflow()
        {
            int visibleStart = Math.Max(_view.TextViewLines.First().Start.Position - _overflow, 0);
            int visibleEnd = Math.Min(_view.TextViewLines.Last().End.Position + _overflow, _buffer.CurrentSnapshot.Length);
            return (visibleStart, visibleEnd);
        }

        private void HandleRatingPrompt()
        {
            if (_tags.Count > 0)
            {
                RatingPrompt prompt = new("MadsKristensen.RainbowBraces", Vsix.Name, General.Instance, 10);
                prompt.RegisterSuccessfulUsage();
            }
        }

        private List<ITagSpan<IClassificationTag>> GenerateTagSpans(IEnumerable<BracePair> pairs, int cycleLength)
        {
            List<ITagSpan<IClassificationTag>> tags = new();

            foreach (BracePair pair in pairs)
            {
                IClassificationType classification = _registry.GetClassificationType(ClassificationTypes.GetName(pair.Level, cycleLength));
                ClassificationTag openTag = new(classification);
                SnapshotSpan openSpan = new(_buffer.CurrentSnapshot, pair.Open);
                tags.Add(new TagSpan<IClassificationTag>(openSpan, openTag));

                ClassificationTag closeTag = new(classification);
                SnapshotSpan closeSpan = new(_buffer.CurrentSnapshot, pair.Close);
                tags.Add(new TagSpan<IClassificationTag>(closeSpan, closeTag));
            }

            return tags;
        }

        private void BuildPairs(List<BracePair> pairs, char match, Span braceSpan, char open, char close)
        {
            if (pairs.Any(p => p.Close == braceSpan || p.Open == braceSpan))
            {
                return;
            }

            int level = pairs.Count(p => p.Close.IsEmpty) + 1;
            BracePair pair = new() { Level = level };

            if (match == open)
            {
                pair.Open = braceSpan;
                pairs.Add(pair);
            }
            else if (match == close)
            {
                pair = pairs.Where(kvp => kvp.Close.IsEmpty).LastOrDefault();
                if (pair != null)
                {
                    pair.Close = braceSpan;
                }
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;
    }
}
