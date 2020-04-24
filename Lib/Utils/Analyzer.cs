﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using AttackSurfaceAnalyzer.Objects;
using AttackSurfaceAnalyzer.Types;
using KellermanSoftware.CompareNetObjects;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AttackSurfaceAnalyzer.Utils
{
    public class Analyzer
    {
        private readonly PLATFORM OsName;
        private RuleFile config;

        private readonly ConcurrentDictionary<(CompareResult, Clause), bool> ClauseCache = new ConcurrentDictionary<(CompareResult, Clause), bool>();
        public Dictionary<RESULT_TYPE, ANALYSIS_RESULT_TYPE> DefaultLevels { get { return config.DefaultLevels; } }

        private static readonly ConcurrentDictionary<string, Regex> RegexCache = new ConcurrentDictionary<string, Regex>();

        public Analyzer(PLATFORM platform, string? filterLocation = null)
        {
            config = new RuleFile();

            if (string.IsNullOrEmpty(filterLocation))
            {
                LoadEmbeddedFilters();
            }
            else
            {
                LoadFilters(filterLocation);
            }
            OsName = platform;
        }

        public Analyzer(PLATFORM platform, RuleFile filters)
        {
            OsName = platform;
            config = filters;
        }

        public List<Rule> Analyze(CompareResult compareResult)
        {
            var results = new List<Rule>();
            if (compareResult == null) { return results; }
            compareResult.Analysis = ANALYSIS_RESULT_TYPE.NONE;
            compareResult.Rules = new List<Rule>();
            var curFilters = config.Rules.Where((rule) => (rule.ChangeTypes == null || rule.ChangeTypes.Contains(compareResult.ChangeType))
                                                     && (rule.Platforms == null || rule.Platforms.Contains(OsName))
                                                     && (rule.ResultType.Equals(compareResult.ResultType)))
                                                    .ToList();

            if (curFilters.Count > 0)
            {
                foreach (Rule rule in curFilters)
                {
                    if (Apply(rule, compareResult))
                    {
                        results.Add(rule);
                    }
                }
            }

            foreach (var item in ClauseCache.Where(x => x.Key.Item1 == compareResult).ToList())
            {
                ClauseCache.Remove(item.Key, out bool _);
            }

            return results;
        }

        public List<string> VerifyRules()
        {
            var violations = new List<string>();

            foreach (Rule rule in config.Rules)
            {
                var clauseLabels = rule.Clauses.GroupBy(x => x.Label);

                // If clauses have duplicate names
                var duplicateClauses = clauseLabels.Where(x => x.Key != null && x.Count() > 1);
                foreach (var duplicateClause in duplicateClauses)
                {
                    violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseDuplicateName"), rule.Name, duplicateClause.Key)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                }

                // If clause label contains illegal characters
                foreach (var clause in rule.Clauses)
                {
                    if (clause.Label is string label)
                    {
                        if (label.Contains(" ") || label.Contains("(") || label.Contains(")"))
                        {
                            violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseInvalidLabel"), rule.Name, label)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                        }
                    }
                    switch (clause.Operation)
                    {
                        case OPERATION.EQ:
                        case OPERATION.NEQ:
                            if ((clause.Data?.Count == null || clause.Data?.Count == 0))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseNoData"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            if (clause.DictData != null || clause.DictData?.Count > 0)
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseDictDataUnexpected"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture), clause.Operation)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            break;
                        case OPERATION.CONTAINS:
                        case OPERATION.CONTAINS_ANY:
                            if ((clause.Data?.Count == null || clause.Data?.Count == 0) && (clause.DictData?.Count == null || clause.DictData?.Count == 0))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseNoDataOrDictData"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            if ((clause.Data is List<string> list && list.Count > 0) && (clause.DictData is List<KeyValuePair<string, string>> dictList && dictList.Count > 0))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseBothDataDictData"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            break;
                        case OPERATION.ENDS_WITH:
                        case OPERATION.STARTS_WITH:
                            if (clause.Data?.Count == null || clause.Data?.Count == 0)
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseNoData"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            if (clause.DictData != null || clause.DictData?.Count > 0)
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseDictDataUnexpected"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture), clause.Operation)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            break;
                        case OPERATION.GT:
                        case OPERATION.LT:
                            if (clause.Data?.Count == null || clause.Data is List<string> clauseList && (clauseList.Count != 1 || !int.TryParse(clause.Data.First(), out int _)))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseExpectedInt"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            if (clause.DictData != null || clause.DictData?.Count > 0)
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseDictDataUnexpected"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture), clause.Operation)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            break;
                        case OPERATION.REGEX:
                            if (clause.Data?.Count == null || clause.Data?.Count == 0)
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseNoData"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            else if (clause.Data is List<string> regexList)
                            {
                                foreach (var regex in regexList)
                                {
                                    if (!AsaHelpers.IsValidRegex(regex))
                                    {
                                        violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseInvalidRegex"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture), regex)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                                    }
                                }
                            }
                            if (clause.DictData != null || clause.DictData?.Count > 0)
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseDictDataUnexpected"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture), clause.Operation)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            break;
                        case OPERATION.IS_NULL:
                        case OPERATION.IS_TRUE:
                        case OPERATION.IS_EXPIRED:
                        case OPERATION.WAS_MODIFIED:
                            if (!(clause.Data?.Count == null || clause.Data?.Count == 0))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseRedundantData"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            else if (!(clause.DictData?.Count == null || clause.DictData?.Count == 0))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseRedundantDictData"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            break;
                        case OPERATION.IS_BEFORE:
                        case OPERATION.IS_AFTER:
                            if (clause.Data?.Count == null || clause.Data is List<string> clauseList2 && (clauseList2.Count != 1 || !DateTime.TryParse(clause.Data.First(), out DateTime _)))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseExpectedDateTime"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture))); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            if (clause.DictData != null || clause.DictData?.Count > 0)
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseDictDataUnexpected"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture), clause.Operation)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            }
                            break;
                        case OPERATION.DOES_NOT_CONTAIN:
                        case OPERATION.DOES_NOT_CONTAIN_ALL:
                        default:
                            violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseUnsuppportedOperator"), rule.Name, clause.Label ?? rule.Clauses.IndexOf(clause).ToString(CultureInfo.InvariantCulture), clause.Operation)); // lgtm [cs/format-argument-unused] - These arguments are defined in the String.Get result
                            break;
                    }
                }

                var foundLabels = new List<string>();

                if (rule.Expression is string expression)
                {
                    // Are parenthesis balanced
                    // Are spaces correct
                    // Are all variables defined by clauses?
                    // Are variables and operators alternating?
                    var splits = expression.Split(" ");
                    int foundStarts = 0;
                    int foundEnds = 0;
                    bool expectingOperator = false;
                    bool previouslyNot = false;
                    for (int i = 0; i < splits.Length; i++)
                    {
                        foundStarts += splits[i].Count(x => x.Equals('('));
                        foundEnds += splits[i].Count(x => x.Equals(')'));
                        if (foundEnds > foundStarts)
                        {
                            violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseUnbalancedParentheses"), expression, rule.Name));
                        }
                        // Variable
                        if (!expectingOperator)
                        {
                            var lastOpen = -1;
                            var lastClose = -1;

                            for (int j = 0; j < splits[i].Length; j++)
                            {
                                // Check that the parenthesis are balanced
                                if (splits[i][j] == '(')
                                {
                                    // If we've seen a ) this is now invalid
                                    if (lastClose != -1)
                                    {
                                        violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseParenthesisInLabel"), expression, rule.Name, splits[i]));
                                    }
                                    // If there were any characters between open parenthesis
                                    if (j - lastOpen != 1)
                                    {
                                        violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseCharactersBetweenOpenParentheses"), expression, rule.Name, splits[i]));
                                    }
                                    // If there was a random parenthesis not starting the variable
                                    else if (j > 0)
                                    {
                                        violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseCharactersBeforeOpenParentheses"), expression, rule.Name, splits[i]));
                                    }
                                    lastOpen = j;
                                }
                                else if (splits[i][j] == ')')
                                {
                                    // If we've seen a close before update last
                                    if (lastClose != -1 && j - lastClose != 1)
                                    {
                                        violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseCharactersBetweenClosedParentheses"), expression, rule.Name, splits[i]));
                                    }
                                    lastClose = j;
                                }
                                else
                                {
                                    // If we've set a close this is invalid because we can't have other characters after it
                                    if (lastClose != -1)
                                    {
                                        violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseCharactersAfterClosedParentheses"), expression, rule.Name, splits[i]));
                                    }
                                }
                            }

                            var variable = splits[i].Replace("(", "").Replace(")", "");

                            if (variable == "NOT")
                            {
                                if (previouslyNot)
                                {
                                    violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseMultipleConsecutiveNots"), expression, rule.Name));
                                }
                                else if (splits[i].Contains(")"))
                                {
                                    violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseCloseParenthesesInNot"), expression, rule.Name, splits[i]));
                                }
                                previouslyNot = true;
                            }
                            else
                            {
                                foundLabels.Add(variable);
                                previouslyNot = false;
                                if (string.IsNullOrWhiteSpace(variable) || !rule.Clauses.Any(x => x.Label == variable))
                                {
                                    violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseUndefinedLabel"), expression, rule.Name, splits[i].Replace("(", "").Replace(")", "")));
                                }
                                expectingOperator = true;
                            }
                        }
                        //Operator
                        else
                        {
                            // If we can't enum parse the operator
                            if (!Enum.TryParse(typeof(BOOL_OPERATOR), splits[i], out object? op))
                            {
                                violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseInvalidOperator"), expression, rule.Name, splits[i]));
                            }
                            // We don't allow NOT operators to modify other Operators, so we can't allow NOT here
                            else
                            {
                                if (op is BOOL_OPERATOR boolOp && boolOp == BOOL_OPERATOR.NOT)
                                {
                                    violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseInvalidNotOperator"), expression, rule.Name));
                                }
                            }
                            expectingOperator = false;
                        }
                    }

                    // We should always end on expecting an operator (having gotten a variable)
                    if (!expectingOperator)
                    {
                        violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseEndsWithOperator"), expression, rule.Name));
                    }
                }

                // Were all the labels declared in clauses used?
                foreach (var label in rule.Clauses.Select(x => x.Label))
                {
                    if (label is string)
                    {
                        if (!foundLabels.Contains(label))
                        {
                            violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseUnusedLabel"), label, rule.Name));
                        }
                    }
                }

                var justTheLabels = clauseLabels.Select(x => x.Key);
                // If any clause has a label they all must have labels
                if (justTheLabels.Any(x => x is string) && justTheLabels.Any(x => x is null))
                {
                    violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseMissingLabels"), rule.Name));
                }
                // If the clause has an expression it may not have any null labels
                if (rule.Expression != null && justTheLabels.Any(x => x is null))
                {
                    violations.Add(string.Format(CultureInfo.InvariantCulture, Strings.Get("Err_ClauseExpressionButMissingLabels"), rule.Name));
                }
            }
            return violations;
        }

        public bool Apply(Rule rule, CompareResult compareResult)
        {
            if (compareResult != null && rule != null)
            {
                // If we have no clauses we automatically match
                if (!rule.Clauses.Any())
                {
                    return true;
                }

                if (rule.Expression == null)
                {
                    if (rule.Clauses.All(x => AnalyzeClause(x, compareResult)))
                    {
                        return true;
                    }
                }
                else
                {
                    if (Evaluate(rule.Expression.Split(" "), rule.Clauses, compareResult))
                    {
                        return true;
                    }
                }

                return false;
            }
            else
            {
                throw new NullReferenceException();
            }
        }

        private static bool Operate(BOOL_OPERATOR Operator, bool first, bool second)
        {
            switch (Operator)
            {
                case BOOL_OPERATOR.AND:
                    return first && second;
                case BOOL_OPERATOR.OR:
                    return first || second;
                case BOOL_OPERATOR.XOR:
                    return first ^ second;
                case BOOL_OPERATOR.NAND:
                    return !(first && second);
                case BOOL_OPERATOR.NOR:
                    return !(first || second);
                case BOOL_OPERATOR.NOT:
                    return !first;
                default:
                    return false;
            }
        }

        private static int FindMatchingParen(string[] splits, int startingIndex)
        {
            int foundStarts = 0;
            int foundEnds = 0;
            for (int i = startingIndex; i < splits.Length; i++)
            {
                foundStarts += splits[i].Count(x => x.Equals('('));
                foundEnds += splits[i].Count(x => x.Equals(')'));

                if (foundStarts <= foundEnds)
                {
                    return i;
                }
            }

            return splits.Length - 1;
        }

        private bool Evaluate(string[] splits, List<Clause> Clauses, CompareResult compareResult)
        {
            bool current = false;

            var invertNextStatement = false;
            var operatorExpected = false;

            BOOL_OPERATOR Operator = BOOL_OPERATOR.OR;

            var updated_i = 0;

            for (int i = 0; i < splits.Length; i = updated_i)
            {
                if (operatorExpected)
                {
                    Operator = (BOOL_OPERATOR)Enum.Parse(typeof(BOOL_OPERATOR), splits[i]);
                    operatorExpected = false;
                    updated_i = i + 1;
                }
                else
                {
                    if (splits[i].StartsWith("("))
                    {
                        //Get the substring closing this paren
                        var matchingParen = FindMatchingParen(splits, i);
                        // If either argument of an AND statement is false,
                        // or either argument of a NOR statement is true,
                        // the result is always false and we can optimize away evaluation of next
                        if ((Operator == BOOL_OPERATOR.AND && current == false) ||
                             (Operator == BOOL_OPERATOR.NOR && current == true))
                        {
                            current = false;
                        }
                        // If either argument of an NAND statement is false,
                        // or either argument of an OR statement is true,
                        // the result is always true and we can optimize away evaluation of next
                        else if ((Operator == BOOL_OPERATOR.OR && current == true) ||
                                   (Operator == BOOL_OPERATOR.NAND && current == false))
                        {
                            current = true;
                        }
                        // If we can't shortcut, do the actual evaluation
                        else
                        {
                            // Recursively evaluate the contents of the parentheses

                            splits[i] = splits[i][1..];
                            splits[matchingParen] = splits[matchingParen][0..^1];
                            var next = Evaluate(splits[i..(matchingParen + 1)], Clauses, compareResult);
                            next = invertNextStatement ? !next : next;
                            current = Operate(Operator, current, next);
                        }
                        updated_i = matchingParen + 1;
                        invertNextStatement = false;
                        operatorExpected = true;
                    }
                    else
                    {
                        if (splits[i].Equals(BOOL_OPERATOR.NOT.ToString()))
                        {
                            invertNextStatement = true;
                            operatorExpected = false;
                        }
                        else
                        {
                            // Ensure we have exactly 1 matching clause defined
                            var res = Clauses.Where(x => x.Label == splits[i].Replace("(", "").Replace(")", ""));
                            if (!(res.Count() == 1))
                            {
                                return false;
                            }
                            // If either argument of an AND statement is false,
                            // or either argument of a NOR statement is true,
                            // the result is always false and we can optimize away evaluation of next
                            if ((Operator == BOOL_OPERATOR.AND && current == false) ||
                                 (Operator == BOOL_OPERATOR.NOR && current == true))
                            {
                                current = false;
                            }
                            // If either argument of an NAND statement is false,
                            // or either argument of an OR statement is true,
                            // the result is always true and we can optimize away evaluation of next
                            else if ((Operator == BOOL_OPERATOR.OR && current == true) ||
                                       (Operator == BOOL_OPERATOR.NAND && current == false))
                            {
                                current = true;
                            }
                            // If we can't shortcut, do the actual evaluation
                            else
                            {
                                var clause = res.First();
                                bool next;
                                if (ClauseCache.TryGetValue((compareResult, clause), out bool cachedValue))
                                {
                                    next = cachedValue;
                                }
                                else
                                {
                                    next = AnalyzeClause(res.First(), compareResult);
                                    ClauseCache.TryAdd((compareResult, clause), next);
                                }

                                next = invertNextStatement ? !next : next;
                                current = Operate(Operator, current, next);
                            }
                            operatorExpected = true;
                        }
                        updated_i = i + 1;
                    }
                }
            }
            return current;
        }

        private static (List<string?>, List<KeyValuePair<string, string>>) ObjectToValues(object? obj)
        {
            var valsToCheck = new List<string?>();
            var dictToCheck = new List<KeyValuePair<string, string>>();
            if (obj != null)
            {
                try
                {
                    if (obj is List<string> stringList)
                    {
                        valsToCheck.AddRange(stringList);
                    }
                    else if (obj is Dictionary<string, string> dictString)
                    {
                        dictToCheck = dictString.ToList();
                    }
                    else if (obj is Dictionary<string, List<string>> dict)
                    {
                        dictToCheck = new List<KeyValuePair<string, string>>();
                        foreach (var list in dict.ToList())
                        {
                            foreach (var entry in list.Value)
                            {
                                dictToCheck.Add(new KeyValuePair<string, string>(list.Key, entry));
                            }
                        }
                    }
                    else if (obj is List<KeyValuePair<string, string>> listKvp)
                    {
                        dictToCheck = listKvp;
                    }
                    else
                    {
                        var val = obj?.ToString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            valsToCheck.Add(val);
                        }
                    }
                }
                catch (Exception e)
                {
                    Dictionary<string, string> ExceptionEvent = new Dictionary<string, string>();
                    ExceptionEvent.Add("Exception Type", e.GetType().ToString());
                    AsaTelemetry.TrackEvent("ApplyDeletedModifiedException", ExceptionEvent);
                }
            }
            else
            {
                valsToCheck.Add(null);
            }

            return (valsToCheck, dictToCheck);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Will gather exception information for analysis via telemetry.")]
        protected static bool AnalyzeClause(Clause clause, CompareResult compareResult)
        {
            if (clause == null || compareResult == null)
            {
                return false;
            }
            try
            {
                object? before = null;
                object? after = null;

                if (compareResult.ChangeType == CHANGE_TYPE.CREATED || compareResult.ChangeType == CHANGE_TYPE.MODIFIED)
                {
                    try
                    {
                        var splits = clause.Field.Split('.');
                        after = GetValueByPropertyName(compareResult.Compare, splits[0]);
                        for (int i = 1; i < splits.Length; i++)
                        {
                            if (after is Dictionary<object, object> dict)
                            {
                                if (dict.TryGetValue(splits[i], out object? value))
                                {
                                    after = value;
                                }
                                else
                                {
                                    after = null;
                                }
                            }
                            else if (after is List<object> list)
                            {
                                if (int.TryParse(splits[i], out int res))
                                {
                                    if (list.Count > res)
                                    {
                                        after = list[res];
                                    }
                                    else
                                    {
                                        after = null;
                                    }
                                }
                            }
                            else
                            {
                                after = GetValueByPropertyName(after, splits[i]);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Information(e, $"Fetching Field {clause.Field} failed from {compareResult.Base?.GetType().ToString() ?? "{null}"}");
                    }
                }
                if (compareResult.ChangeType == CHANGE_TYPE.DELETED || compareResult.ChangeType == CHANGE_TYPE.MODIFIED)
                {
                    try
                    {
                        var splits = clause.Field.Split('.');
                        before = GetValueByPropertyName(compareResult.Base, splits[0]);
                        for (int i = 1; i < splits.Length; i++)
                        {
                            if (before is Dictionary<string, string> dict)
                            {
                                if (dict.TryGetValue(splits[i], out string? value))
                                {
                                    before = value;
                                }
                                else
                                {
                                    before = null;
                                }
                            }
                            else if (before is List<string> list)
                            {
                                if (int.TryParse(splits[i], out int res))
                                {
                                    if (list.Count > res)
                                    {
                                        before = list[res];
                                    }
                                    else
                                    {
                                        before = null;
                                    }
                                }
                            }
                            else
                            {
                                before = GetValueByPropertyName(before, splits[i]);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Information(e, $"Fetching Field {clause.Field} failed from {compareResult.Base?.GetType().ToString() ?? "{null}"}");
                    }
                }

                var typeHolder = before is null ? after : before;

                (var beforeList, var beforeDict) = ObjectToValues(before);
                (var afterList, var afterDict) = ObjectToValues(after);

                var valsToCheck = beforeList.Union(afterList);
                var dictToCheck = beforeDict.Union(afterDict);

                switch (clause.Operation)
                {
                    case OPERATION.EQ:
                        if (clause.Data is List<string> EqualsData)
                        {
                            if (EqualsData.Intersect(valsToCheck).Any())
                            {
                                return true;
                            }
                        }
                        return false;

                    case OPERATION.NEQ:
                        if (clause.Data is List<string> NotEqualsData)
                        {
                            if (!NotEqualsData.Intersect(valsToCheck).Any())
                            {
                                return true;
                            }
                        }
                        return false;


                    // If *every* entry of the clause data is matched
                    case OPERATION.CONTAINS:
                        if (dictToCheck.Any())
                        {
                            if (clause.DictData is List<KeyValuePair<string, string>> ContainsData)
                            {
                                if (ContainsData.All(y => dictToCheck.Where((x) => x.Key == y.Key && x.Value == y.Value).Any()))
                                {
                                    return true;
                                }
                            }
                        }
                        else if (valsToCheck.Any())
                        {
                            if (clause.Data is List<string> ContainsDataList)
                            {
                                // If we are dealing with an array on the object side
                                if (typeHolder is List<string>)
                                {
                                    if (ContainsDataList.All(x => valsToCheck.Contains(x)))
                                    {
                                        return true;
                                    }
                                }
                                // If we are dealing with a single string we do a .Contains instead
                                else if (typeHolder is string)
                                {
                                    if (clause.Data.All(x => valsToCheck.First()?.Contains(x) ?? false))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        return false;

                    // If *any* entry of the clause data is matched
                    case OPERATION.CONTAINS_ANY:
                        if (dictToCheck.Any())
                        {
                            if (clause.DictData is List<KeyValuePair<string, string>> ContainsData)
                            {
                                foreach (KeyValuePair<string, string> value in ContainsData)
                                {
                                    if (dictToCheck.Any(x => x.Key == value.Key && x.Value == value.Value))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        else if (valsToCheck.Any())
                        {
                            if (clause.Data is List<string> ContainsDataList)
                            {
                                if (typeHolder is List<string>)
                                {
                                    if (ContainsDataList.Any(x => valsToCheck.Contains(x)))
                                    {
                                        return true;
                                    }
                                }
                                // If we are dealing with a single string we do a .Contains instead
                                else if (typeHolder is string)
                                {
                                    if (clause.Data.Any(x => valsToCheck.First()?.Contains(x) ?? false))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        return false;

                    // If any of the data values are greater than the first provided clause value
                    // We ignore all other clause values
                    case OPERATION.GT:
                        foreach (var val in valsToCheck)
                        {
                            if (int.TryParse(val, out int valToCheck))
                            {
                                if (int.TryParse(clause.Data?[0], out int dataValue))
                                {
                                    if (valToCheck > dataValue)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        return false;

                    // If any of the data values are less than the first provided clause value
                    // We ignore all other clause values
                    case OPERATION.LT:
                        foreach (var val in valsToCheck)
                        {
                            if (int.TryParse(val, out int valToCheck))
                            {
                                if (int.TryParse(clause.Data?[0], out int dataValue))
                                {
                                    if (valToCheck < dataValue)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                        return false;

                    // If any of the regexes match any of the values
                    case OPERATION.REGEX:
                        if (clause.Data is List<string> RegexList)
                        {
                            if (RegexList.Count > 0)
                            {
                                var built = string.Join('|', RegexList);

                                if (!RegexCache.ContainsKey(built))
                                {
                                    try
                                    {
                                        RegexCache.TryAdd(built, new Regex(built, RegexOptions.Compiled));
                                    }
                                    catch (ArgumentException)
                                    {
                                        Log.Warning("InvalidArgumentException when analyzing clause {0}. Regex {1} is invalid and will be skipped.", clause.Label, built);
                                        RegexCache.TryAdd(built, new Regex("", RegexOptions.Compiled));
                                    }
                                }

                                if (valsToCheck.Any(x => x != null && RegexCache[built].IsMatch(x)))
                                {
                                    return true;
                                }
                            }
                        }
                        return false;

                    // Ignores provided data. Checks if the named property has changed.
                    case OPERATION.WAS_MODIFIED:
                        if (compareResult.ChangeType == CHANGE_TYPE.MODIFIED)
                        {
                            CompareLogic compareLogic = new CompareLogic();
                            ComparisonResult result = compareLogic.Compare(before, after);

                            return !result.AreEqual;
                        }
                        return false;

                    // Ends with any of the provided data
                    case OPERATION.ENDS_WITH:
                        if (clause.Data is List<string> EndsWithData)
                        {
                            if (valsToCheck.Any(x => EndsWithData.Any(y => x is string && x.EndsWith(y, StringComparison.CurrentCulture))))
                            {
                                return true;
                            }
                        }
                        return false;

                    // Starts with any of the provided data
                    case OPERATION.STARTS_WITH:
                        if (clause.Data is List<string> StartsWithData)
                        {
                            if (valsToCheck.Any(x => StartsWithData.Any(y => x is string && x.StartsWith(y, StringComparison.CurrentCulture))))
                            {
                                return true;
                            }
                        }
                        return false;

                    case OPERATION.IS_NULL:
                        if (valsToCheck.Count(x => x is null) == valsToCheck.Count())
                        {
                            return true;
                        }
                        return false;

                    case OPERATION.IS_TRUE:
                        foreach (var valToCheck in valsToCheck)
                        {
                            if (bool.TryParse(valToCheck, out bool result))
                            {
                                if (result)
                                {
                                    return true;
                                }
                            }
                        }
                        return false;

                    case OPERATION.IS_BEFORE:
                        var valDateTimes = new List<DateTime>();
                        foreach (var valToCheck in valsToCheck)
                        {
                            if (DateTime.TryParse(valToCheck, out DateTime result))
                            {
                                valDateTimes.Add(result);
                            }
                        }
                        foreach (var data in clause.Data ?? new List<string>())
                        {
                            if (DateTime.TryParse(data, out DateTime result))
                            {
                                if (valDateTimes.Any(x => x.CompareTo(result) < 0))
                                {
                                    return true;
                                }
                            }
                        }
                        return false;

                    case OPERATION.IS_AFTER:
                        valDateTimes = new List<DateTime>();
                        foreach (var valToCheck in valsToCheck)
                        {
                            if (DateTime.TryParse(valToCheck, out DateTime result))
                            {
                                valDateTimes.Add(result);
                            }
                        }
                        foreach (var data in clause.Data ?? new List<string>())
                        {
                            if (DateTime.TryParse(data, out DateTime result))
                            {
                                if (valDateTimes.Any(x => x.CompareTo(result) > 0))
                                {
                                    return true;
                                }
                            }
                        }
                        return false;

                    case OPERATION.IS_EXPIRED:
                        foreach (var valToCheck in valsToCheck)
                        {
                            if (DateTime.TryParse(valToCheck, out DateTime result))
                            {
                                if (result.CompareTo(DateTime.Now) < 0)
                                {
                                    return true;
                                }
                            }
                        }
                        return false;

                    default:
                        Log.Debug("Unimplemented operation {0}", clause.Operation);
                        return false;
                }
            }
            catch (Exception e)
            {
                Log.Debug(e, $"Hit while parsing {JsonConvert.SerializeObject(clause)} onto {JsonConvert.SerializeObject(compareResult)}");
                Dictionary<string, string> ExceptionEvent = new Dictionary<string, string>();
                ExceptionEvent.Add("Exception Type", e.GetType().ToString());
                AsaTelemetry.TrackEvent("ApplyOverallException", ExceptionEvent);
            }

            return false;
        }

        private static object? GetValueByPropertyName(object? obj, string? propertyName) => obj?.GetType().GetProperty(propertyName ?? string.Empty)?.GetValue(obj);


        public void DumpFilters()
        {
            Log.Verbose("Filter dump:");
            Log.Verbose(JsonConvert.SerializeObject(config));
        }

        public void LoadEmbeddedFilters()
        {
            try
            {
                var assembly = typeof(FileSystemObject).Assembly;
                var resourceName = "AttackSurfaceAnalyzer.analyses.json";
                using (Stream stream = assembly.GetManifestResourceStream(resourceName) ?? new MemoryStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    config = JsonConvert.DeserializeObject<RuleFile>(reader.ReadToEnd());
                    Log.Information(Strings.Get("LoadedAnalyses"), "Embedded");
                }
                if (config == null)
                {
                    Log.Debug("No filters today.");
                    return;
                }
                DumpFilters();
            }
            catch (Exception e) when (
                e is ArgumentNullException
                || e is ArgumentException
                || e is FileLoadException
                || e is FileNotFoundException
                || e is BadImageFormatException
                || e is NotImplementedException)
            {

                config = new RuleFile();
                Log.Debug("Could not load filters {0} {1}", "Embedded", e.GetType().ToString());

                // This is interesting. We shouldn't hit exceptions when loading the embedded resource.
                Dictionary<string, string> ExceptionEvent = new Dictionary<string, string>();
                ExceptionEvent.Add("Exception Type", e.GetType().ToString());
                AsaTelemetry.TrackEvent("EmbeddedAnalysesFilterLoadException", ExceptionEvent);
            }
        }

        public void LoadFilters(string filterLoc = "")
        {
            if (!string.IsNullOrEmpty(filterLoc))
            {
                try
                {
                    using (StreamReader file = System.IO.File.OpenText(filterLoc))
                    {
                        config = JsonConvert.DeserializeObject<RuleFile>(file.ReadToEnd());
                        Log.Information(Strings.Get("LoadedAnalyses"), filterLoc);
                    }
                    if (config == null)
                    {
                        Log.Debug("No filters this time.");
                        return;
                    }
                    DumpFilters();
                }
                catch (Exception e) when (
                    e is UnauthorizedAccessException
                    || e is ArgumentException
                    || e is ArgumentNullException
                    || e is PathTooLongException
                    || e is DirectoryNotFoundException
                    || e is FileNotFoundException
                    || e is NotSupportedException)
                {
                    config = new RuleFile();
                    //Let the user know we couldn't load their file
                    Log.Warning(Strings.Get("Err_MalformedFilterFile"), filterLoc);

                    return;
                }
            }

        }
    }
}


