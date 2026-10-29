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
                CheckColumns(doc, table, name, findings);
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
                }
            }
        }

        private void CheckColumns(XDocument doc, string table, string formName, List<Finding> findings)
        {
            var columns = GetColumns(table);
            if (columns == null)
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
        internal static bool IsDefined(string content, string leaf)
        {
            var flat = Regex.Replace(StripComments(content), @"\s+", " ");

            var shapes = new[]
            {
                leaf + " : function",
                leaf + ": function",
                leaf + " = function",
                leaf + "= function",
                leaf + " : async function",
                leaf + " = async function",
                "function " + leaf + " (",
                "function " + leaf + "(",
                leaf + " : (",
                leaf + " = (",
                leaf + " : async (",
                leaf + " = async ("
            };

            if (shapes.Any(shape => flat.IndexOf(shape, StringComparison.Ordinal) >= 0))
            {
                return true;
            }

            // ES6 shorthand, e.g.  onLoad (context) { ... }
            // The trailing brace is what separates a definition from a plain call such as
            // onLoad(context); - without it, any call would be mistaken for a definition and
            // real problems would be missed.
            var shorthand = new Regex(Regex.Escape(leaf) + @"\s*\([^)]*\)\s*\{");
            return shorthand.IsMatch(flat);
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
            try
            {
                var request = new RetrieveEntityRequest
                {
                    LogicalName = table,
                    EntityFilters = EntityFilters.Attributes
                };
                var response = (RetrieveEntityResponse)_service.Execute(request);
                set = new HashSet<string>(
                    response.EntityMetadata.Attributes.Select(a => a.LogicalName),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // A table we cannot read is skipped rather than reported as broken.
                set = null;
            }

            _columns[table] = set;
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
