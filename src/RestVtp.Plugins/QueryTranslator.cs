using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace RestVtp.Plugins
{
    /// <summary>
    /// Translates the QueryExpression Dataverse hands to RetrieveMultiple
    /// into query-string parameters for the target REST API.
    ///
    /// v1 contract (fail loudly, never silently drop a filter):
    ///   - AND-combined equality conditions on columns listed in
    ///     filterParams. Anything else -> InvalidPluginExecutionException.
    ///   - Retrieve-by-id shortcut: a single Equal condition on the primary
    ///     key column is recognised and surfaced via KeyLookup.
    ///   - Paging via PageInfo (pageNumber or skip/top modes).
    ///   - Ordering only on columns in sortFields; otherwise throw
    ///     (strictSort=true) or trace-and-ignore (default).
    /// </summary>
    public sealed class TranslatedQuery
    {
        public Dictionary<string, string> QueryParams { get; } = new Dictionary<string, string>();
        /// <summary>Set when the query is a primary-key equality lookup.</summary>
        public string KeyLookup { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; }
        public int? TopCount { get; set; }
    }

    public static class QueryTranslator
    {
        public static TranslatedQuery Translate(
            QueryExpression query, TableMapping map, ITracingService trace)
        {
            var result = new TranslatedQuery();

            // ----- filters -----
            if (query.Criteria != null)
                TranslateFilter(query.Criteria, map, result, trace);

            // ----- ordering -----
            if (query.Orders != null && query.Orders.Count > 0)
            {
                var sortTerms = new List<string>();
                foreach (var order in query.Orders)
                {
                    if (map.SortFields.TryGetValue(order.AttributeName, out var apiField)
                        && !string.IsNullOrEmpty(map.SortParam))
                    {
                        sortTerms.Add(order.OrderType == OrderType.Descending
                            ? map.SortDescPrefix + apiField
                            : apiField);
                    }
                    else if (map.StrictSort)
                    {
                        throw new InvalidPluginExecutionException(
                            $"RestVtp: sorting on '{order.AttributeName}' is not supported by the mapped API.");
                    }
                    else
                    {
                        trace.Trace($"RestVtp: ignoring unsupported sort on '{order.AttributeName}'.");
                    }
                }
                if (sortTerms.Count > 0)
                    result.QueryParams[map.SortParam] = string.Join(",", sortTerms);
            }

            // ----- paging -----
            var paging = map.Paging ?? new PagingConfig();
            int pageSize = paging.MaxPageSize;
            int pageNumber = 1;

            if (query.PageInfo != null && query.PageInfo.Count > 0)
            {
                pageSize = Math.Min(query.PageInfo.Count, paging.MaxPageSize);
                pageNumber = Math.Max(query.PageInfo.PageNumber, 1);
            }
            if (query.TopCount.HasValue)
            {
                result.TopCount = query.TopCount.Value;
                pageSize = Math.Min(pageSize, query.TopCount.Value);
            }

            result.PageNumber = pageNumber;
            result.PageSize = pageSize;

            switch ((paging.Mode ?? "none").ToLowerInvariant())
            {
                case "pagenumber":
                    result.QueryParams[paging.PageParam] = pageNumber.ToString();
                    result.QueryParams[paging.SizeParam] = pageSize.ToString();
                    break;
                case "skiptop":
                    result.QueryParams[paging.SkipParam] = ((pageNumber - 1) * pageSize).ToString();
                    result.QueryParams[paging.TopParam] = pageSize.ToString();
                    break;
                case "none":
                    // API returns everything; provider slices the page locally.
                    // Acceptable only for small collections, documented constraint.
                    break;
                default:
                    throw new InvalidPluginExecutionException(
                        $"RestVtp: unknown paging mode '{paging.Mode}'.");
            }

            return result;
        }

        private static void TranslateFilter(
            FilterExpression filter, TableMapping map, TranslatedQuery result, ITracingService trace)
        {
            // OR filters: only acceptable if the whole thing is a quick-find
            // we can't serve, so throw with a clear message instead of wrong data.
            if (filter.FilterOperator == LogicalOperator.Or &&
                (filter.Conditions.Count + filter.Filters.Count) > 1)
            {
                throw new InvalidPluginExecutionException(
                    "RestVtp: OR filters (including quick find) are not supported in v1. " +
                    "Filter on a single column with '=' instead.");
            }

            foreach (var child in filter.Filters)
                TranslateFilter(child, map, result, trace);

            foreach (var cond in filter.Conditions)
                TranslateCondition(cond, map, result);
        }

        private static void TranslateCondition(
            ConditionExpression cond, TableMapping map, TranslatedQuery result)
        {
            // Primary-key lookup (grids/forms do this constantly)
            if (string.Equals(cond.AttributeName, map.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase)
                && cond.Operator == ConditionOperator.Equal)
            {
                var id = cond.Values.FirstOrDefault();
                var guid = id is Guid g ? g : Guid.Parse(id.ToString());
                result.KeyLookup = GuidSynthesizer.FromGuid(map.KeyKind, guid);
                return;
            }

            if (cond.Operator != ConditionOperator.Equal)
            {
                throw new InvalidPluginExecutionException(
                    $"RestVtp: operator '{cond.Operator}' on '{cond.AttributeName}' is not supported in v1. " +
                    "Only '=' filters on mapped columns are translated.");
            }

            if (!map.FilterParams.TryGetValue(cond.AttributeName, out var paramName))
            {
                throw new InvalidPluginExecutionException(
                    $"RestVtp: column '{cond.AttributeName}' is not filterable: the mapped API " +
                    "exposes no query parameter for it. Add it to filterParams or remove the filter.");
            }

            var value = cond.Values.FirstOrDefault();
            result.QueryParams[paramName] = Convert.ToString(
                value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
