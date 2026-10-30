using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FormWiringValidator.Model;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace FormWiringValidator.Checks
{
    /// <summary>
    /// Reads every main form and reports handlers that cannot possibly work.
    ///
    /// None of these problems surface anywhere else: the solution imports cleanly, nothing is
    /// written to a log, and the first sign of trouble is an error dialog in a user's face, or a
    /// handler that quietly never runs.
    /// </summary>
    public class FormWiringChecker
    {
        // Controls that point at another table's data. Their datafieldname is not a column on the
        // form's own table, so checking it would only produce noise.
        private static readonly HashSet<string> NonFieldControls = new HashSet<string>(
            new[]
            {
                "{E7A81278-8635-4D9E-8D4D-59480B391C5B}", // sub-grid
                "{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}", // web resource
                "{F9A8A302-114E-466A-B582-6771B2AE0D92}"  // timeline
            },
            StringComparer.OrdinalIgnoreCase);

        private readonly IOrganizationService _service;

        private readonly Dictionary<string, string> _libraries =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, HashSet<string>> _columns =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, bool> _skipColumnCheck =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public int FormsScanned { get; private set; }

        public int HandlersChecked { get; private set; }

        public FormWiringChecker(IOrganizationService service)
        {
            _service = service;
        }

        public List<Finding> Run(ICollection<string> prefixes, Action<string> progress)
        {
            var findings = new List<Finding>();
            var forms = RetrieveMainForms(progress);

            var done = 0;
            foreach (var form in forms)
            {
                done++;
                if (done % 25 == 0 && progress != null)
                {
                    progress("Checking form " + done + " of " + forms.Count + "...");
                }

                var xml = form.GetAttributeValue<string>("formxml");
                if (string.IsNullOrWhiteSpace(xml))
                {
                    continue;
                }

                XDocument doc;
                try
                {
                    doc = XDocument.Parse(xml);
                }
                catch (Exception)
                {
                    // A form we cannot parse is a gap in this tool, not a finding to report.
                    continue;
                }

                FormsScanned++;
                var table = form.GetAttributeValue<string>("objecttypecode");
                var name = form.GetAttributeValue<string>("name");

                CheckHandlers(doc, table, name, prefixes, findings);
                CheckColumns(doc, table, name, prefixes, findings);
                CheckLibraryOrder(doc, table, name, prefixes, findings);
            }

            return findings;
        }

        private void CheckHandlers(XDocument doc, string table, string formName,
            ICollection<string> prefixes, List<Finding> findings)
        {
            var declared = new HashSet<string>(
                doc.Descendants("Library")
                   .Select(l => (string)l.Attribute("name"))
                   .Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var handler in doc.Descendants("Handler"))
            {
                var function = (string)handler.Attribute("functionName");
                var library = (string)handler.Attribute("libraryName");

                if (string.IsNullOrWhiteSpace(function) || string.IsNullOrWhiteSpace(library))
                {
                    continue;
                }

                if (!BelongsToCustomer(library, prefixes))
                {
                    continue;
                }

                HandlersChecked++;
                var eventName = EventNameOf(handler);

                if (!declared.Contains(library))
                {
                    findings.Add(new Finding
                    {
                        Severity = Severity.Error,
                        Table = table,
                        Form = formName,
                        Event = eventName,
                        Handler = function,
                        Library = library,
                        Problem = "The handler uses a library that the form does not load",
                        Fix = "Add the library to the form, or point the handler at one already loaded"
                    });
                    continue;
                }

                var content = GetLibrary(library);

                if (content == null)
                {
                    findings.Add(new Finding
                    {
                        Severity = Severity.Error,
                        Table = table,
                        Form = formName,
                        Event = eventName,
                        Handler = function,
                        Library = library,
                        Problem = "The web resource does not exist in this environment",
                        Fix = "Deploy the web resource, or remove the handler"
                    });
                    continue;
                }

                if (content.Trim().Length == 0)
                {
                    findings.Add(new Finding
                    {
                        Severity = Severity.Error,
                        Table = table,
                        Form = formName,
                        Event = eventName,
                        Handler = function,
                        Library = library,
                        Problem = "The web resource exists but is empty",
                        Fix = "Upload the script content and publish"
                    });
                    continue;
                }

                var leaf = function.Split('.').Last();
                if (!IsDefined(content, leaf))
                {
                    findings.Add(new Finding
                    {
                        Severity = Severity.Error,
                        Table = table,
                        Form = formName,
                        Event = eventName,
                        Handler = function,
                        Library = library,
                        Problem = leaf + " is not defined in " + library,
                        Fix = "Correct the function name on the handler, or add the function to the library"
                    });
                    continue;
                }

                var passes = string.Equals((string)handler.Attribute("passExecutionContext"),
                    "true", StringComparison.OrdinalIgnoreCase);

                if (!passes && NeedsExecutionContext(content, leaf))
                {
                    findings.Add(new Finding
                    {
                        Severity = Severity.Error,
                        Table = table,
                        Form = formName,
                        Event = eventName,
                        Handler = function,
                        Library = library,
                        Problem = leaf + " uses the execution context, but the handler does not pass it",
                        Fix = "Tick 'Pass execution context as first parameter' on the handler"
                    });
                }
            }
        }

        private void CheckColumns(XDocument doc, string table, string formName,
            ICollection<string> prefixes, List<Finding> findings)
        {
            // Microsoft's own forms bind to Microsoft's own columns, and a stale control on one of
            // them is not something anyone here can act on. Report a missing column only where the
            // table or the column belongs to the customer, otherwise the handful of findings that
            // matter are buried under dozens that do not.
            var ourTable = BelongsToCustomer(table, prefixes);

            var columns = GetColumns(table);
            if (columns == null || _skipColumnCheck[table])
            {
                return;
            }

            foreach (var control in doc.Descendants("control"))
            {
                var field = (string)control.Attribute("datafieldname");
                if (string.IsNullOrWhiteSpace(field))
                {
                    continue;
                }

                var classId = (string)control.Attribute("classid");
                if (!string.IsNullOrEmpty(classId) && NonFieldControls.Contains(classId))
                {
                    continue;
                }

                if (!ourTable && !BelongsToCustomer(field, prefixes))
                {
                    continue;
                }

                if (!columns.Contains(field))
                {
                    findings.Add(new Finding
                    {
                        Severity = Severity.Warning,
                        Table = table,
                        Form = formName,
                        Event = "(control)",
                        Handler = field,
                        Library = string.Empty,
                        Problem = "The form binds to a column that no longer exists on " + table,
                        Fix = "Remove the control from the form, or restore the column"
                    });
                }
            }
        }

        /// <summary>
        /// Libraries load in the order the form lists them, so one that builds on another has to
        /// come after it. Get that wrong and the form throws on open - which is exactly how this
        /// tool came to exist.
        /// </summary>
        private void CheckLibraryOrder(XDocument doc, string table, string formName,
            ICollection<string> prefixes, List<Finding> findings)
        {
            var libraries = doc.Descendants("Library")
                .Select(l => (string)l.Attribute("name"))
                .Where(n => !string.IsNullOrEmpty(n) && BelongsToCustomer(n, prefixes))
                .ToList();

            if (libraries.Count < 2)
            {
                return;
            }

            var defines = new List<HashSet<string>>();
            var uses = new List<HashSet<string>>();

            foreach (var name in libraries)
            {
                var content = GetLibrary(name);
                if (content == null)
                {
                    defines.Add(new HashSet<string>());
                    uses.Add(new HashSet<string>());
                    continue;
                }

                var flat = Regex.Replace(StripComments(content), @"\s+", " ");

                // Savills.entities = { ... }
                var declared = new HashSet<string>(
                    Regex.Matches(flat, @"([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+)\s*=\s*\{")
                        .Cast<Match>().Select(m => m.Groups[1].Value),
                    StringComparer.Ordinal);

                // Only what runs as the file loads can be hurt by the order. Anything inside a
                // function runs later, by which time every library is in memory - so counting
                // those would report load order problems that do not exist. In one real
                // environment that was the difference between nine findings and none.
                var atLoad = WithoutFunctionBodies(flat);

                var referenced = new HashSet<string>(
                    Regex.Matches(atLoad, @"([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+)\.")
                        .Cast<Match>().Select(m => m.Groups[1].Value),
                    StringComparer.Ordinal);

                referenced.ExceptWith(declared);
                defines.Add(declared);
                uses.Add(referenced);
            }

            for (var i = 0; i < libraries.Count; i++)
            {
                foreach (var symbol in uses[i])
                {
                    for (var j = i + 1; j < libraries.Count; j++)
                    {
                        if (!defines[j].Contains(symbol))
                        {
                            continue;
                        }

                        findings.Add(new Finding
                        {
                            Severity = Severity.Error,
                            Table = table,
                            Form = formName,
                            Event = "(libraries)",
                            Handler = symbol,
                            Library = libraries[i],
                            Problem = libraries[i] + " uses " + symbol + ", which " + libraries[j] + " defines - but that loads later",
                            Fix = "Move " + libraries[j] + " above " + libraries[i] + " in the form's libraries"
                        });
                        break;
                    }
                }
            }
        }

        private static bool BelongsToCustomer(string library, ICollection<string> prefixes)
        {
            return prefixes.Any(p => library.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase));
        }

        private static string EventNameOf(XElement handler)
        {
            var element = handler.Parent;
            while (element != null)
            {
                if (string.Equals(element.Name.LocalName, "event", StringComparison.OrdinalIgnoreCase))
                {
                    return (string)element.Attribute("name") ?? "(event)";
                }
                element = element.Parent;
            }
            return "(event)";
        }

        /// <summary>
        /// Looks for a definition of the function rather than a mention of it, so that a call or a
        /// comment is not taken as proof that the function exists.
        /// </summary>
        public static bool IsDefined(string content, string leaf)
        {
            var flat = Regex.Replace(StripComments(content), @"\s+", " ");
            var name = Regex.Escape(leaf);

            // Assigned to a property or a variable:
            //   onLoad: function (e)      onLoad = function (e)
            //   onLoad: async function    onLoad: (e) =>        onLoad: async (e) =>
            if (Regex.IsMatch(flat, name + @"\s*[:=]\s*(async\s+)?(function\b|\()"))
            {
                return true;
            }

            // Arrow function with a single unbracketed parameter:  onLoad: e => { }
            if (Regex.IsMatch(flat, name + @"\s*[:=]\s*(async\s+)?[A-Za-z_$][\w$]*\s*=>"))
            {
                return true;
            }

            // Declared:  function onLoad (e)
            if (Regex.IsMatch(flat, @"\bfunction\s+" + name + @"\s*\("))
            {
                return true;
            }

            // ES6 shorthand method:  onLoad (context) { ... }
            // The trailing brace is what separates a definition from a plain call such as
            // onLoad(context); - without it any call would be mistaken for a definition and
            // real problems would be missed.
            return Regex.IsMatch(flat, name + @"\s*\([^)]*\)\s*\{");
        }

        /// <summary>
        /// True when the function's first parameter is used as an execution context.
        ///
        /// This is the quietest failure of the lot. The handler runs, the first argument is
        /// undefined, and the script dies on the first line with nothing written anywhere a
        /// maker would look.
        ///
        /// Only the function's own body is read, so a different function using the same
        /// parameter name cannot make this report something that is actually fine.
        /// </summary>
        public static bool NeedsExecutionContext(string content, string leaf)
        {
            var flat = Regex.Replace(StripComments(content), @"\s+", " ");

            var open = ParameterListStart(flat, leaf);
            if (open < 0)
            {
                return false;
            }

            var close = flat.IndexOf(')', open);
            if (close < 0)
            {
                return false;
            }

            var parameters = flat.Substring(open + 1, close - open - 1)
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (parameters.Count == 0)
            {
                return false;
            }

            var body = BodyAfter(flat, close);
            if (body == null)
            {
                return false;
            }

            var first = parameters[0];
            return body.IndexOf(first + ".getFormContext(", StringComparison.Ordinal) >= 0
                || body.IndexOf(first + ".getEventSource(", StringComparison.Ordinal) >= 0
                || body.IndexOf(first + ".getEventArgs(", StringComparison.Ordinal) >= 0
                || body.IndexOf(first + ".getDepth(", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Index of the "(" that opens the function's parameter list.</summary>
        private static int ParameterListStart(string flat, string leaf)
        {
            var name = Regex.Escape(leaf);

            var assigned = Regex.Match(flat, name + @"\s*[:=]\s*(async\s+)?function\b");
            if (assigned.Success)
            {
                return flat.IndexOf('(', assigned.Index + assigned.Length);
            }

            var declared = Regex.Match(flat, @"\bfunction\s+" + name + @"\s*\(");
            if (declared.Success)
            {
                return flat.IndexOf('(', declared.Index);
            }

            var arrow = Regex.Match(flat, name + @"\s*[:=]\s*(async\s+)?\(");
            if (arrow.Success)
            {
                return flat.IndexOf('(', arrow.Index + leaf.Length);
            }

            var shorthand = Regex.Match(flat, name + @"\s*\([^)]*\)\s*\{");
            if (shorthand.Success)
            {
                return flat.IndexOf('(', shorthand.Index + leaf.Length);
            }

            return -1;
        }

        // Words that are followed by "( ... ) {" but are not a method definition.
        private static readonly HashSet<string> ControlWords = new HashSet<string>(
            new[] { "if", "for", "while", "switch", "catch", "function", "do", "else", "with" },
            StringComparer.Ordinal);

        /// <summary>
        /// Blanks out every function body, leaving only what executes while the file is loading.
        ///
        /// Two passes are needed. The first catches the classic forms, which announce themselves
        /// with "function" or "=>". The second catches ES6 shorthand methods - onLoad(context) { }
        /// - which carry no keyword at all, and whose bodies would otherwise be read as load-time
        /// code and produce load order findings that are not real.
        /// </summary>
        public static string WithoutFunctionBodies(string js)
        {
            var pass = BlankBodies(js, (text, i) =>
                (i + 8 <= text.Length && string.CompareOrdinal(text, i, "function", 0, 8) == 0)
                || (i + 2 <= text.Length && string.CompareOrdinal(text, i, "=>", 0, 2) == 0)
                    ? text.IndexOf('{', i)
                    : -1);

            return BlankBodies(pass, (text, i) =>
            {
                if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'
                              || text[i - 1] == '$' || text[i - 1] == '.'))
                {
                    return -1;
                }

                var m = Regex.Match(text.Substring(i, Math.Min(200, text.Length - i)),
                    @"^([A-Za-z_$][\w$]*)\s*\([^()]*\)\s*\{");
                if (!m.Success || ControlWords.Contains(m.Groups[1].Value))
                {
                    return -1;
                }

                return i + m.Length - 1;
            });
        }

        private static string BlankBodies(string js, Func<string, int, int> openingBraceAt)
        {
            var chars = js.ToCharArray();

            for (var i = 0; i < js.Length; i++)
            {
                var open = openingBraceAt(js, i);
                if (open < 0 || open >= js.Length || js[open] != '{')
                {
                    continue;
                }

                var depth = 0;
                for (var k = open; k < js.Length; k++)
                {
                    if (js[k] == '{')
                    {
                        depth++;
                    }
                    else if (js[k] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            for (var b = open; b <= k; b++)
                            {
                                chars[b] = ' ';
                            }
                            i = k;
                            break;
                        }
                    }
                }
            }

            return new string(chars);
        }

        /// <summary>The braced block that follows, found by matching braces.</summary>
        private static string BodyAfter(string flat, int from)
        {
            var open = flat.IndexOf('{', from);
            if (open < 0)
            {
                return null;
            }

            var depth = 0;
            for (var i = open; i < flat.Length; i++)
            {
                if (flat[i] == '{')
                {
                    depth++;
                }
                else if (flat[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return flat.Substring(open, i - open + 1);
                    }
                }
            }
            return null;
        }

        private static string StripComments(string js)
        {
            var withoutBlocks = Regex.Replace(js, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\r\n]*", " ");
        }

        private string GetLibrary(string name)
        {
            string cached;
            if (_libraries.TryGetValue(name, out cached))
            {
                return cached;
            }

            var query = new QueryExpression("webresource")
            {
                ColumnSet = new ColumnSet("content"),
                Criteria = new FilterExpression()
            };
            query.Criteria.AddCondition("name", ConditionOperator.Equal, name);

            string content = null;
            var rows = _service.RetrieveMultiple(query).Entities;
            if (rows.Count > 0)
            {
                var encoded = rows[0].GetAttributeValue<string>("content");
                content = string.IsNullOrEmpty(encoded)
                    ? string.Empty
                    : Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }

            _libraries[name] = content;
            return content;
        }

        private HashSet<string> GetColumns(string table)
        {
            HashSet<string> cached;
            if (_columns.TryGetValue(table, out cached))
            {
                return cached;
            }

            HashSet<string> set;
            var skip = false;
            try
            {
                var request = new RetrieveEntityRequest
                {
                    LogicalName = table,
                    EntityFilters = EntityFilters.Attributes
                };
                var response = (RetrieveEntityResponse)_service.Execute(request);
                var metadata = response.EntityMetadata;

                set = new HashSet<string>(
                    metadata.Attributes.Select(a => a.LogicalName),
                    StringComparer.OrdinalIgnoreCase);

                // A business process flow has its own table, but its form binds to columns of the
                // table the process runs on, not to its own. Checking those columns here would
                // report every one of them as missing, which is wrong and drowns the real
                // findings. The same goes for the hidden join tables behind many-to-many
                // relationships.
                skip = (metadata.IsBPFEntity.HasValue && metadata.IsBPFEntity.Value)
                    || (metadata.IsIntersect.HasValue && metadata.IsIntersect.Value);
            }
            catch (Exception)
            {
                // A table we cannot read is skipped rather than reported as broken.
                set = null;
            }

            _columns[table] = set;
            _skipColumnCheck[table] = skip;
            return set;
        }

        private List<Entity> RetrieveMainForms(Action<string> progress)
        {
            var all = new List<Entity>();
            var query = new QueryExpression("systemform")
            {
                ColumnSet = new ColumnSet("name", "objecttypecode", "formxml"),
                Criteria = new FilterExpression(),
                PageInfo = new PagingInfo { Count = 250, PageNumber = 1 }
            };
            query.Criteria.AddCondition("type", ConditionOperator.Equal, 2);

            while (true)
            {
                var page = _service.RetrieveMultiple(query);
                all.AddRange(page.Entities);

                if (progress != null)
                {
                    progress("Loaded " + all.Count + " forms...");
                }

                if (!page.MoreRecords)
                {
                    break;
                }

                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }

            return all;
        }
    }
}
