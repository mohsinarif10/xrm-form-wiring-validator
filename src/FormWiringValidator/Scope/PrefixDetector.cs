using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace FormWiringValidator.Scope
{
    /// <summary>
    /// Works out which publisher prefixes belong to the customer.
    ///
    /// This matters more than any single check. An environment carries hundreds of Microsoft
    /// forms whose scripts are bundled inside the platform and cannot be resolved from a web
    /// resource, so checking them produces noise and nothing else. Scanning only the customer's
    /// own prefixes is what turns a flood of false positives into a short, real list.
    /// </summary>
    public static class PrefixDetector
    {
        private static readonly HashSet<string> PlatformPrefixes = new HashSet<string>(
            new[] { "msdyn", "msdynce", "msdyncrm", "msdynmkt", "mspp", "cc", "adx", "msfp",
                    "msgp", "msevtmgt", "msra", "mspcat", "msdynsm", "bot", "ms" },
            StringComparer.OrdinalIgnoreCase);

        public static List<string> Detect(IOrganizationService service)
        {
            var query = new QueryExpression("publisher")
            {
                ColumnSet = new ColumnSet("customizationprefix", "friendlyname")
            };

            return service.RetrieveMultiple(query).Entities
                .Select(e => e.GetAttributeValue<string>("customizationprefix"))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !PlatformPrefixes.Contains(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
