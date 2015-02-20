﻿using AutoMapper;
using Microsoft.Data.OData.Query;
using Microsoft.Data.OData.Query.SemanticAst;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http.OData.Query;

namespace Microsoft.Windows.Azure.Service.DynamicsCrm
{
    internal class QueryExpressionBuilder<TTableData,TEntity>
    {
        protected string EntityLogicalName { get; set; }
        protected ODataQueryOptions ODataQueryOptions { get; set; }
        protected Dictionary<String, IMemberAccessor> PropertyMap { get; set; }

        public QueryExpressionBuilder(string entityLogicalName, ODataQueryOptions query)
        {
            EntityLogicalName = entityLogicalName;
            ODataQueryOptions = query;
            var map = Mapper.FindTypeMapFor<TTableData, TEntity>();
            if (map == null) throw new InvalidOperationException(String.Format("Could not find a map from {0} to {1}.", typeof(TTableData), typeof(TEntity)));

            this.PropertyMap = map.GetPropertyMaps().ToDictionary(m => m.SourceMember.Name, m => m.DestinationProperty, StringComparer.OrdinalIgnoreCase);
                               
        }

        public QueryExpression GetQueryExpression()
        {
            QueryExpression crmQuery = new QueryExpression(this.EntityLogicalName);

            ODataValidationSettings settings = new ODataValidationSettings();
            
            settings.AllowedLogicalOperators =
                AllowedLogicalOperators.Equal |
                AllowedLogicalOperators.NotEqual |
                AllowedLogicalOperators.GreaterThan |
                AllowedLogicalOperators.GreaterThanOrEqual |
                AllowedLogicalOperators.LessThan |
                AllowedLogicalOperators.LessThanOrEqual |
                AllowedLogicalOperators.And |
                AllowedLogicalOperators.Or;
            
            settings.AllowedFunctions = 
                AllowedFunctions.StartsWith | 
                AllowedFunctions.SubstringOf | 
                AllowedFunctions.EndsWith;

            settings.AllowedArithmeticOperators = AllowedArithmeticOperators.None;

            settings.MaxTop = 5000;
            settings.AllowedQueryOptions = AllowedQueryOptions.All ^ AllowedQueryOptions.Expand;

            this.ODataQueryOptions.Validate(settings);

            var map = Mapper.FindTypeMapFor<TTableData, TEntity>();
            var propertyMaps = map.GetPropertyMaps();

            UpdateCriteriaFromFilter(crmQuery.Criteria, ODataQueryOptions.Filter);
            UpdateColumnSetFromSelectExpand(crmQuery.ColumnSet, ODataQueryOptions.SelectExpand);
            UpdatePagingFromSkipAndTop(crmQuery.PageInfo, ODataQueryOptions.Skip, ODataQueryOptions.Top);
            UpdateOrdersFromOrderBy(crmQuery.Orders, ODataQueryOptions.OrderBy);

            return crmQuery;
        }

        private void UpdateCriteriaFromFilter(FilterExpression criteria, FilterQueryOption filter)
        {
            if (filter != null)
            {
                if (filter.Context != null)
                {
                    if (filter.Context.ElementType.TypeKind != Microsoft.Data.Edm.EdmTypeKind.Entity)
                    {
                        throw new NotImplementedException(String.Format("Unsupported OData element type kind: {0}", filter.Context.ElementType.TypeKind));
                    }

                    if (filter.Context.ElementClrType != typeof(TTableData))
                    {
                        throw new InvalidOperationException(String.Format("Unexpected OData element type: {0}", filter.Context.ElementType));
                    }
                }

                UpdateCriteriaFromExpression(criteria, filter.FilterClause.Expression);
            }
        }

        private void UpdateCriteriaFromExpression(FilterExpression criteria, SingleValueNode expression)
        {
            switch (expression.Kind)
            {
                case QueryNodeKind.BinaryOperator:
                    UpdateCriteriaFromBinaryExpression(criteria, (BinaryOperatorNode)expression);
                    break;

                case QueryNodeKind.Convert:
                    UpdateCriteriaFromExpression(criteria, ((ConvertNode)expression).Source);
                    break;

                case QueryNodeKind.SingleValueFunctionCall:
                    UpdateCriteriaFromSingleValueFunctionCall(criteria, (SingleValueFunctionCallNode)expression);
                    break;

                default:
                    throw new NotImplementedException(String.Format("Unsupported expression kind: \'{0}\'.", expression.Kind));
            }
        }

        private void UpdateCriteriaFromBinaryExpression(FilterExpression criteria, BinaryOperatorNode expression)
        {
            ConditionOperator crmOperator;
            switch (expression.OperatorKind)
            {
                case BinaryOperatorKind.And:
                case BinaryOperatorKind.Or:
                    var childCriteria = new FilterExpression(expression.OperatorKind == BinaryOperatorKind.And ? LogicalOperator.And : LogicalOperator.Or);
                    criteria.AddFilter(childCriteria);
                    UpdateCriteriaFromExpression(childCriteria, expression.Left);
                    UpdateCriteriaFromExpression(childCriteria, expression.Right);
                    return;

                case BinaryOperatorKind.Equal:
                    if (expression.Left.Kind == QueryNodeKind.SingleValueFunctionCall &&
                        expression.Right.Kind == QueryNodeKind.Constant &&
                        (bool)((ConstantNode)expression.Right).Value == true)
                    {
                        UpdateCriteriaFromSingleValueFunctionCall(criteria, (SingleValueFunctionCallNode)expression.Left);
                        return;
                    }
                    else
                    {
                        crmOperator = ConditionOperator.Equal;
                    }
                    break;

                case BinaryOperatorKind.NotEqual:
                    crmOperator = ConditionOperator.NotEqual;
                    break;

                case BinaryOperatorKind.GreaterThan:
                    crmOperator = ConditionOperator.GreaterThan;
                    break;

                case BinaryOperatorKind.GreaterThanOrEqual:
                    crmOperator = ConditionOperator.GreaterEqual;
                    break;

                case BinaryOperatorKind.LessThan:
                    crmOperator = ConditionOperator.LessThan;
                    break;

                case BinaryOperatorKind.LessThanOrEqual:
                    crmOperator = ConditionOperator.LessEqual;
                    break;

                default:
                    throw new NotImplementedException(String.Format("Unsupported operator \'{0}\'.", expression.OperatorKind));
            }

            var value = GetValue(expression.Right);
            var attributeName = GetAttributeName(expression.Left);

            if (value == null)
            {
                if (crmOperator == ConditionOperator.Equal) crmOperator = ConditionOperator.Null;
                if (crmOperator == ConditionOperator.NotEqual) crmOperator = ConditionOperator.NotNull;
                
                criteria.AddCondition(attributeName, crmOperator);
            }
            else
            {
                criteria.AddCondition(attributeName, crmOperator, value);
            }
        }

        private void UpdateCriteriaFromSingleValueFunctionCall(FilterExpression criteria, SingleValueFunctionCallNode expression)
        {
            QueryNode[] arguments = expression.Arguments.ToArray();

            switch (expression.Name.ToLowerInvariant())
            {
                case "startswith":
                    if (arguments.Length != 2) throw new InvalidOperationException("\'startswith\' expects 2 arguments.");
                    criteria.AddCondition(GetAttributeName(arguments[0]), ConditionOperator.BeginsWith, GetValue(arguments[1]));
                    break;

                case "endswith":
                    if (arguments.Length != 2) throw new InvalidOperationException("\'endswith\' expects 2 arguments.");
                    criteria.AddCondition(GetAttributeName(arguments[0]), ConditionOperator.EndsWith, GetValue(arguments[1]));
                    break;

                case "substringof":
                    if (arguments.Length != 2) throw new InvalidOperationException("\'substringof\' expects 2 arguments.");
                    criteria.AddCondition(GetAttributeName(arguments[0]), ConditionOperator.Contains, GetValue(arguments[1]));
                    break;

                default:
                    throw new NotImplementedException(String.Format("Unsupported function \'{0}\'.", expression.Name.ToLowerInvariant()));
            }
        }

        private static object GetValue(QueryNode queryNode)
        {
            switch (queryNode.Kind)
            {
                case QueryNodeKind.Constant:
                    return ((ConstantNode)queryNode).Value;

                case QueryNodeKind.Convert:
                    return GetValue(((ConvertNode)queryNode).Source);

                default:
                    throw new NotImplementedException(String.Format("Unsupported value type \'{0}\'.", queryNode.Kind));
            }
        }

        private string GetAttributeName(QueryNode queryNode)
        {
            switch (queryNode.Kind)
            {
                case QueryNodeKind.Convert:
                    return GetAttributeName(((ConvertNode)queryNode).Source);

                case QueryNodeKind.SingleValuePropertyAccess:
                    var prop = GetDestinationProperty(((SingleValuePropertyAccessNode)queryNode).Property.Name);
                    return prop.Name.ToLowerInvariant();

                default:
                    throw new NotImplementedException(String.Format("Unsupported property selector type \'{0}\'.", queryNode.Kind));
            }
        }

        private IMemberAccessor GetDestinationProperty(string sourcePropertyName)
        {
            return this.PropertyMap[sourcePropertyName];
        }

        private void UpdateColumnSetFromSelectExpand(ColumnSet columnSet, SelectExpandQueryOption selectExpand)
        {
            if (selectExpand == null || selectExpand.SelectExpandClause == null || selectExpand.SelectExpandClause.AllSelected)
            {
                foreach (var destProp in this.PropertyMap.Values)
                {
                    columnSet.AddColumn(destProp.Name.ToLowerInvariant());
                }
            }
            else
            {
                foreach (var item in selectExpand.SelectExpandClause.SelectedItems.OfType<PathSelectItem>())
                {
                    var pathItem = item.SelectedPath.OfType<PropertySegment>().Single();
                    var destProp = GetDestinationProperty(pathItem.Property.Name);
                    columnSet.AddColumn(destProp.Name.ToLowerInvariant());
                }
            }

            columnSet.Columns.Remove("id");
        }

        private static void UpdatePagingFromSkipAndTop(PagingInfo pagingInfo, SkipQueryOption skip, TopQueryOption top)
        {
            if (top == null)
            {
                pagingInfo.Count = 50;
            }
            else
            {
                pagingInfo.Count = top.Value;
            }

            if (skip == null)
            {
                pagingInfo.PageNumber = 1;
            }
            else
            {
                pagingInfo.PageNumber = skip.Value / pagingInfo.Count + 1;
            }
        }

        private void UpdateOrdersFromOrderBy(DataCollection<OrderExpression> orders, OrderByQueryOption orderBy)
        {
            if (orderBy != null)
            {
                foreach (var node in orderBy.OrderByNodes.OfType<OrderByPropertyNode>())
                {
                    var destProp = GetDestinationProperty(node.Property.Name);
                    var direction = node.Direction == OrderByDirection.Ascending ? OrderType.Ascending : OrderType.Descending;
                    orders.Add(new OrderExpression(destProp.Name.ToLowerInvariant(), direction));
                }
            }
        }
    }
}
