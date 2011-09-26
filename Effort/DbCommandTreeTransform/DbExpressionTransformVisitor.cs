﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.Common.CommandTrees;
using System.Linq.Expressions;
using System.Data.Metadata.Edm;
using MMDB.EntityFrameworkProvider.TypeGeneration;
using MMDB.EntityFrameworkProvider.Helpers;
using System.Reflection;
using System.Data.SqlClient;
using System.Data.Common;
using MMDB.EntityFrameworkProvider.DbCommandTreeTransform.Join;
using MMDB.Linq.Visitors;
using MMDB.Linq.StoredProcedures;

namespace MMDB.EntityFrameworkProvider.DbCommandTreeTransform
{
    public class DbExpressionTransformVisitor : DbExpressionVisitor<Expression>
    {
        private Dictionary<string, Tuple<TypeUsage, int>> parameters;
        private ParameterExpression[] parameterExpressions;

        private ITableProvider tableProvider;
        private IMethodProvider methodProvider;


        private QueryMethodExpressionBuilder queryMethodExpressionBuilder;
        private EdmTypeConverter typeConverter;
        private CanonicalFunctionMapper functionMapper;

        private Stack<ContextHandler> contextStack;

        public DbExpressionTransformVisitor()
        {
            this.queryMethodExpressionBuilder = new QueryMethodExpressionBuilder();
            this.typeConverter = new EdmTypeConverter();
            this.contextStack = new Stack<ContextHandler>();
            this.parameters = new Dictionary<string, Tuple<TypeUsage, int>>();
            this.functionMapper = new CanonicalFunctionMapper();

            //TODO: Set from outside
            this.methodProvider = new MMDB.EntityFrameworkProvider.DatabaseManagement.MethodProvider();
        }

        public ITableProvider TableProvider
        {
            set { this.tableProvider = value; }
            get { return this.tableProvider; }
        }

        public IMethodProvider MethodProvider
        {
            set { this.methodProvider = value; }
            get { return this.methodProvider; }
        }

        public void SetParameters(KeyValuePair<string, TypeUsage>[] parameters)
        {
            this.parameters.Clear();

            for (int i = 0; i < parameters.Length; i++)
			{
			    this.parameters.Add(parameters[i].Key, new Tuple<TypeUsage, int>(parameters[i].Value, i));
			}

            this.parameterExpressions = new ParameterExpression[parameters.Length];
        }

        public ParameterExpression[] GetParameterExpressions()
        {
            if (this.parameterExpressions == null)
            {
                throw new InvalidOperationException();
            }

            return this.parameterExpressions.ToArray();
        }

        #region Context management

        public ContextHandler CreateContext(Expression contextParam, string name)
        {
            Context context = new Context();
            context.SourceExpression = contextParam;
            context.Name = name;

            ContextHandler handler = new ContextHandler(context, this.contextStack);
            this.contextStack.Push(handler);
            return handler;
        }

        private Context GetContext(string name)
        {
            if (contextStack.Count == 0)
            {
                throw new InvalidOperationException("No context has been created");
            }

            if (name == null)
            {
                return contextStack.Peek().Context;
            }

            foreach (var item in contextStack)
            {
                if (item.Context.Name == name)
                {
                    return item.Context;
                }
            }

            throw new InvalidOperationException(name + " context was not found");
        }

        private Context GetContext()
        {
            return this.GetContext(null);
        }

        #endregion

        public override Expression Visit(DbExpression expression)
        {
            //Expression recalls the specific Visit method
            return expression.Accept(this);
        }

        private Expression Visit(DbExpression expression, Type requiredType)
        {
            Expression result = this.Visit(expression);

            if (result.Type != requiredType)
            {
                result = Expression.Convert(result, requiredType);
            }

            return result;
        }


        //??
        public override Expression Visit(DbUnionAllExpression expression)
        {
            Expression left = this.Visit(expression.Left);
            Expression right = this.Visit(expression.Right);

            Type resultType = typeConverter.Convert(expression.ResultType).GetElementType();
            Type rightType = TypeHelper.GetEncapsulatedType(right.Type);


            ParameterExpression param = Expression.Parameter(rightType);

            List<MemberBinding> bindings = new List<MemberBinding>();

            PropertyInfo[] sourceProps = rightType.GetProperties();
            PropertyInfo[] resultProps = resultType.GetProperties();

            List<Expression> initializers = new List<Expression>();

            for (int i = 0; i < sourceProps.Length; i++)
            {
                initializers.Add(Expression.Property(param, sourceProps[i]));
            }

            Expression body = Expression.New(resultType.GetConstructors().Single(), initializers, resultType.GetProperties());
            right = queryMethodExpressionBuilder.Select(right, Expression.Lambda(body, param));
            
            return queryMethodExpressionBuilder.Concat(left, right);
        }


        //tamas
        public override Expression Visit(DbTreatExpression expression)
        {
            throw new NotImplementedException();
        }

        //tamas
        public override Expression Visit(DbLimitExpression expression)
        {
            Expression source = this.Visit(expression.Argument);
            Type sourceType = TypeHelper.GetEncapsulatedType(source.Type);

            return queryMethodExpressionBuilder.Take(source, this.Visit(expression.Limit, typeof(int)));
        }

        //tamas
        public override Expression Visit(DbSkipExpression expression)
        {
            Expression source = this.Visit(expression.Input.Expression);
            Type sourceType = TypeHelper.GetEncapsulatedType(source.Type);

            //Skip cannot be used without sorting
            Expression result = this.CreateOrder(expression.SortOrder, expression.Input.VariableName, source);

            return queryMethodExpressionBuilder.Skip(result, this.Visit(expression.Count, typeof(int)));
        }

        //zsolt
        public override Expression Visit(DbSortExpression expression)
        {
            Expression source = this.Visit(expression.Input.Expression);

            return this.CreateOrder(expression.SortOrder, expression.Input.VariableName, source);
        }

        private Expression CreateOrder(IList<DbSortClause> sortorder, string sourceVariableName, Expression source)
        {
            Type sourceType = TypeHelper.GetEncapsulatedType(source.Type);

            Expression result = source;
            bool first = true;
            foreach (DbSortClause sort in sortorder)
            {
                ParameterExpression param = Expression.Parameter(sourceType, sourceVariableName);
                using (this.CreateContext(param, sourceVariableName))
                {
                    LambdaExpression selector = Expression.Lambda(this.Visit(sort.Expression), param);
                 
                    if (sort.Ascending)
                    {
                        if( first )
                        {
                            result = queryMethodExpressionBuilder.OrderBy( result, selector );
                            first = false;
                        }
                        else
                        {
                            result = queryMethodExpressionBuilder.ThenBy( result, selector );
                        }
                    }
                    else
                    {
                        if( first )
                        {
                            result = queryMethodExpressionBuilder.OrderByDescending( result, selector );
                            first = false;
                        }
                        else
                        {
                            result = queryMethodExpressionBuilder.ThenByDescending( result, selector );
                        }
                    }
                }
            }

            return result;
        }

        //tamas
        public override Expression Visit(DbScanExpression expression)
        {
            if (tableProvider == null)
            {
                throw new InvalidOperationException("TableProvider is not set");
            }

            return Expression.Constant(tableProvider.GetTable(expression.Target.Name));
        }


        //zsolt
        public override Expression Visit(DbRelationshipNavigationExpression expression)
        {
            throw new NotImplementedException();
        }

        //zsolt
        public override Expression Visit(DbQuantifierExpression expression)
        {
            throw new NotImplementedException();
        }

        public override Expression Visit(DbPropertyExpression expression)
        {
            string propertyName = expression.Property.Name;

            Expression sourceExpression = this.Visit(expression.Instance);
            Expression propertyExpression = Expression.Property(sourceExpression, propertyName);

            //return propertyExpression;

            Type nullablePropertyType = TypeHelper.MakeNullable(propertyExpression.Type);
            if (nullablePropertyType != propertyExpression.Type)
            {
                propertyExpression = Expression.Convert(propertyExpression, nullablePropertyType);
            }
                
            Expression condition = 
                Expression.MakeBinary(
                    ExpressionType.NotEqual, 
                    sourceExpression, 
                    Expression.Constant(null));

            Expression result = 
                Expression.Condition(
                    condition, 
                    propertyExpression, 
                    Expression.Constant(null, propertyExpression.Type));

            return result;
        }

        #region


        public override Expression Visit(DbProjectExpression expression)
        {
            Expression source = this.Visit(expression.Input.Expression);

            Type sourceType = TypeHelper.GetEncapsulatedType(source.Type);

            ParameterExpression param = Expression.Parameter(sourceType, expression.Input.VariableName);
            using (this.CreateContext(param, expression.Input.VariableName))
            {
                LambdaExpression projectionExpression = Expression.Lambda(this.Visit(expression.Projection), param);

                return queryMethodExpressionBuilder.Select(source, projectionExpression);
            }
        }

        public override Expression Visit(DbFilterExpression expression)
        {
            Expression source = this.Visit(expression.Input.Expression);
            Type sourceType = TypeHelper.GetEncapsulatedType(source.Type);

            ParameterExpression param = Expression.Parameter(sourceType, expression.Input.VariableName);
            using (this.CreateContext(param, expression.Input.VariableName))
            {
                LambdaExpression predicate = Expression.Lambda(this.Visit(expression.Predicate), param);

                return queryMethodExpressionBuilder.Where(source, predicate);
            }
        }

        //tamas
        public override Expression Visit(DbGroupByExpression expression)
        {
            Expression source = this.Visit(expression.Input.Expression);
            Type sourceType = TypeHelper.GetEncapsulatedType(source.Type);

            Type resultType = typeConverter.GetEncapsulatedType(expression.ResultType);
            Expression result = source;

            if (expression.Keys.Count == 0)
            {
                //This is a special case
                //The DbGroupByExpression does not contain any Key element
                //Its a simple aggregate function

                if (expression.Aggregates.Count != 1)
                {
                    throw new InvalidOperationException("The DbGroupByExpression does not contain any Key element, but has more than one Aggregate element");
                }

                if (!(expression.Aggregates[0] is DbFunctionAggregate))
                {
                    throw new InvalidOperationException("The DbGroupByExpression does not contain any Key element, but the only Aggregate element is not DbFunctionAggregate");
                }

                Expression aggregation = this.CreateAggregateFunction(
                    expression.Aggregates[0] as DbFunctionAggregate,
                    //Aggregation is executed on the source
                    expression.Input.GroupVariableName,
                    sourceType,
                    source,
                    resultType.GetProperties()[0].PropertyType);

                Expression singleResult =
                    Expression.New(
                        resultType.GetConstructors().Single(),
                        new Expression[] { aggregation },
                        resultType.GetProperties());

                result =
                    Expression.New(
                        typeof(SingleResult<>).MakeGenericType(resultType).GetConstructors().Single(),
                        singleResult);
            }
            else
            {

                //The properties of the selector form a subset of the properties of the result type
                //These properties defined first in the edm type
                PropertyInfo[] props = resultType.GetProperties();
                Dictionary<string, Type> selectorProperties = new Dictionary<string, Type>();

                //Collect the properties
                for (int i = 0;i < expression.Keys.Count;i++)
                {
                    selectorProperties.Add(props[i].Name, props[i].PropertyType);
                }

                Type selectorType = AnonymousTypeFactory.Create(selectorProperties);
                LambdaExpression selector = null;

                ParameterExpression groupParam = Expression.Parameter(sourceType, expression.Input.VariableName);
                using (this.CreateContext(groupParam, expression.Input.VariableName))
                {
                    selector =
                        Expression.Lambda(
                            this.CreateSelector(expression.Keys, selectorType),
                            groupParam);
                }

                //Build the GroupBy call expression
                result = queryMethodExpressionBuilder.GroupBy(result, selector);

                //Get IGrouping<> type
                Type groupingType = TypeHelper.GetEncapsulatedType(result.Type);
                //Collect argument initiators in an array
                Expression[] groupInit = new Expression[expression.Keys.Count + expression.Aggregates.Count];

                ParameterExpression selectParam = Expression.Parameter(groupingType, "group");
                Expression keyParam = Expression.Property(selectParam, "Key");
                //Collect the Key arguments

                for (int i = 0;i < expression.Keys.Count;i++)
                {
                    groupInit[i] = Expression.Property(keyParam, props[i].Name);
                }


                //Collect the aggregate arguments
                for (int i = 0;i < expression.Aggregates.Count;i++)
                {
                    DbAggregate aggregate = expression.Aggregates[i];

                    if (aggregate is DbFunctionAggregate)
                    {
                        groupInit[expression.Keys.Count + i] =
                            this.CreateAggregateFunction(
                                aggregate as DbFunctionAggregate,
                            //Aggregation is executed on the group
                                expression.Input.GroupVariableName,
                                sourceType,
                                selectParam,
                                props[expression.Keys.Count + i].PropertyType);
                    }
                    else
                    {
                        throw new NotSupportedException("Only DbFunctionAggregate is supported");
                    }
                }

                selector =
                    Expression.Lambda(
                        Expression.New(
                            resultType.GetConstructors().Single(),
                            groupInit,
                            resultType.GetProperties()),
                        selectParam);

                result = queryMethodExpressionBuilder.Select(result, selector);
            }

            return result;
        }


        private Expression CreateAggregateFunction(DbFunctionAggregate functionAggregate, string sourceVariableName, Type sourceType, Expression sourceGroup, Type resultType)
        {
            Expression result = null;

            //More the one aggregate argument is not supported
            if (functionAggregate.Arguments.Count > 1)
            {
                throw new InvalidOperationException("DbFunctionAggreate contains more than one argument");
            }

            //Build the selector of the current aggregate
            LambdaExpression aggregateSelector = null;
            if (functionAggregate.Arguments.Count == 1)
            {
                ParameterExpression aggregateContext = Expression.Parameter(sourceType, sourceVariableName);
                using (this.CreateContext(aggregateContext, sourceVariableName))
                {
                    aggregateSelector =
                        Expression.Lambda(
                            this.Visit(functionAggregate.Arguments[0]),
                            aggregateContext);
                }
            }

            //Create Expression Call
            switch (functionAggregate.Function.Name)
            {
                case "Count":
                    result = queryMethodExpressionBuilder.Count(sourceGroup);
                    break;

                case "Max":
                    result = queryMethodExpressionBuilder.Max(sourceGroup, aggregateSelector);
                    break;

                case "Min":
                    result = queryMethodExpressionBuilder.Min(sourceGroup, aggregateSelector);
                    break;

                case "Avg":
                    result = queryMethodExpressionBuilder.Average(sourceGroup, aggregateSelector);
                    break;

                case "Sum":
                    result = queryMethodExpressionBuilder.Sum(sourceGroup, aggregateSelector);
                    break;

                default:
                    throw new NotSupportedException(functionAggregate.Function.Name + " is not a not supported DbFunctionAggregate");
            }

            //Type unify
            if (resultType != null && result.Type != resultType)
            {
                result = Expression.Convert(result, resultType);
            }

            return result;
        }

        //tamas
        public override Expression Visit(DbNewInstanceExpression expression)
        {
            Type resultType = typeConverter.Convert(expression.ResultType);
            
            return this.CreateSelector(expression.Arguments, resultType);
        }

        private Expression CreateSelector(IList<DbExpression> arguments, Type resultType)
        {
            List<Expression> resultTypeArgumentInitializerExpressions = new List<Expression>();
            PropertyInfo[] props = resultType.GetProperties();


            if( resultType.IsArray )
            {
                for( int i = 0 ; i < arguments.Count ; i++ )
                {
                    Expression argumentExpression = this.Visit( arguments[i] );

                    resultTypeArgumentInitializerExpressions.Add( argumentExpression );
                }

                Expression array = 
                    Expression.NewArrayInit( 
                        resultType.GetElementType(), 
                        resultTypeArgumentInitializerExpressions.ToArray() );

                Type listType = typeof(List<>).MakeGenericType( resultType.GetElementType() );

                var constr = listType
                    .GetConstructors()
                    .Where(c => 
                        c.GetParameters().Length == 1 &&
                        c.GetParameters()[0].ParameterType.IsGenericType &&
                        c.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    .First();
                
                Expression list = Expression.New(constr, array);

                Expression queryable = 
                    Expression.Call(
                        typeof( Queryable )
                            .GetMethods().Where( m => m.Name == "AsQueryable" &&  m.IsGenericMethod ).Single()
                            .MakeGenericMethod(listType.GetGenericArguments()[0]),
                        list );
                
                return queryable;
            }
            else
            {
                for (int i = 0; i < arguments.Count;i++)
                {
                    Expression argumentExpression = this.Visit(arguments[i], props[i].PropertyType);

                    resultTypeArgumentInitializerExpressions.Add(argumentExpression);
                }

                return Expression.New(
                    resultType.GetConstructors().Single(),
                    resultTypeArgumentInitializerExpressions,
                    resultType.GetProperties());
            }
        }

        #endregion

        #region References

        public override Expression Visit(DbParameterReferenceExpression expression)
        {
            //object value = this.parameterValues[expression.ParameterName].Value;

            //typeConverter.Convert(this.parameters[expression.ParameterName]);

            Type type = typeConverter.Convert(expression.ResultType);

            var parameterPlaceholderConstructor = typeof(MMDBParameter<>)
                .MakeGenericType(type)
                .GetConstructors()
                .Single( c => c.GetParameters().Count() == 1 );

            var parameter = Expression.New( parameterPlaceholderConstructor, Expression.Constant( expression.ParameterName ) );
            var convertedParameter = Expression.Convert( parameter, type );

            return convertedParameter;
            //int index = this.parameters[expression.ParameterName].Item2;

            //this.parameterExpressions[index] = parameter;


            //return parameter;
        }

        public override Expression Visit(DbVariableReferenceExpression expression)
        {
            return this.GetContext(expression.VariableName).SourceExpression;
        }

        public override Expression Visit(DbRefExpression expression)
        {
            throw new NotImplementedException();
        }

        #endregion

        public override Expression Visit(DbOfTypeExpression expression)
        {
            throw new NotImplementedException();
        }

        public override Expression Visit(DbNullExpression expression)
        {
            return Expression.Constant(null, typeConverter.Convert(expression.ResultType));
        }

        #region Predicate primitives

        public override Expression Visit(DbNotExpression expression)
        {
            return Expression.Not(this.Visit(expression.Argument));
        }

        public override Expression Visit(DbAndExpression expression)
        {
            // zsvarnai - atirtam AndAlso-ra, mert bitwise AND volt
            return Expression.AndAlso(
                this.Visit(expression.Left),
                this.Visit(expression.Right));
        }

        public override Expression Visit(DbOrExpression expression)
        {
            return
                Expression.OrElse(
                    this.Visit(expression.Left),
                    this.Visit(expression.Right));
        }

        #endregion

        //zsolt
        public override Expression Visit(DbLikeExpression expression)
        {
            Expression argumentExpression = this.Visit(expression.Argument);
            Expression patternExpression = this.Visit(expression.Pattern);

            return Expression.Call(null, this.methodProvider.Like, argumentExpression, patternExpression);
        }

        //zsolt: ez a BMK dolog nekem kell valamire
        //BMK Join
        public override Expression Visit(DbJoinExpression expression )
        {
            Expression left = this.Visit(expression.Left.Expression);
            Expression right = this.Visit(expression.Right.Expression);

            ParameterExpression leftParam = Expression.Parameter(left.Type.GetGenericArguments().First(), "p0");
            ParameterExpression rightParam = Expression.Parameter(right.Type.GetGenericArguments().First(), "p1");

            LambdaExpression firstKeySelector;
            LambdaExpression secondKeySelector;

            using (this.CreateContext( leftParam, expression.Left.VariableName))
            using( this.CreateContext( rightParam, expression.Right.VariableName ) )
            {
                Expression joinCondition = this.Visit( expression.JoinCondition );

                DbJoinConditionVisitor v = new DbJoinConditionVisitor();

                v.Visit( expression.JoinCondition );

                var leftExpressions = v.LeftSide.Select( dbExp => this.Visit( dbExp ) ).ToList();
                var rightExpressions = v.RightSide.Select( dbExp => this.Visit( dbExp ) ).ToList();

                ParameterFinderVisitor pfv = new ParameterFinderVisitor();

                foreach (var exp in leftExpressions)
                {
                    pfv.Visit(exp);
                }

                if( pfv.UsedParameters.Contains( rightParam ) )
                {
                    if (pfv.UsedParameters.Contains(leftParam))
                    {
                        throw new InvalidOperationException();
                    }
                    else
                    {
                        // A join feltetel pont forditott sorrendben van

                        var swap = leftExpressions;
                        leftExpressions = rightExpressions;
                        rightExpressions = swap;
                    }
                }

                Expression leftArrayInit = Expression.NewArrayInit( typeof( object ),
                    leftExpressions.Select( exp => Expression.Convert( exp, typeof(object))).ToArray() );

                Expression rightArrayInit = Expression.NewArrayInit( typeof( object ),
                    rightExpressions.Select( exp => Expression.Convert( exp, typeof( object ) ) ).ToArray() );

                ConstructorInfo propListConstructor = typeof(PropertyList).GetConstructors().First();

                Expression leftNewPropertyList = Expression.New( propListConstructor, leftArrayInit );
                Expression rightNewPropertyList = Expression.New( propListConstructor, rightArrayInit );

                firstKeySelector = Expression.Lambda( leftNewPropertyList, leftParam );
                secondKeySelector = Expression.Lambda( rightNewPropertyList, rightParam );
            
            }

            //using (this.CreateContext(leftParam, expression.Left.VariableName))
            //{
            //    Expression leftSelectorBody = this.Visit((expression.JoinCondition as DbComparisonExpression).Left);
            //    firstKeySelector = Expression.Lambda(leftSelectorBody, leftParam);

            //}
            //using (this.CreateContext(rightParam, expression.Right.VariableName))
            //{
            //    Expression rightSelectorBody = this.Visit((expression.JoinCondition as DbComparisonExpression).Right);
            //    secondKeySelector = Expression.Lambda(rightSelectorBody, rightParam);
            //}


            Expression result = queryMethodExpressionBuilder.Join(left, right, expression.Left.VariableName,
                            expression.Right.VariableName, firstKeySelector, secondKeySelector, expression.ExpressionKind);

            return result;
        }



        //??
        public override Expression Visit(DbIsOfExpression expression)
        {
            throw new NotImplementedException();
        }

        //tamas
        public override Expression Visit(DbIsNullExpression expression)
        {
            Expression exp = this.Visit(expression.Argument);

            // zsvarnai:
            // mivel nalunk az ideiglenes sorok nem mind nullable tipusuak, ezert elofordulhat olyan, 
            // hogy nem nullable elemen hivodik DbIsNull
            // Ez ilyenkor nem hasonlitja, hanem hamissal ter vissza
            // Azt azert majd le kell tesztelni, hogy logikailag jo nem okozunk-e ezzel gondot

            if (exp.Type.IsValueType && !TypeHelper.IsNullable(exp.Type))
            {
                return Expression.Constant(false);
            }
            else
            {
                return Expression.Equal(exp, Expression.Constant(null));
            }
        }

        //zsolt
        public override Expression Visit(DbIsEmptyExpression expression)
        {
            Expression arg = this.Visit( expression.Argument );

            return Expression.Not( queryMethodExpressionBuilder.Any( arg ) );
        }


        //zsolt
        public override Expression Visit(DbIntersectExpression expression)
        {
            Expression left = this.Visit(expression.Left);
            Expression right = this.Visit(expression.Right);

            return queryMethodExpressionBuilder.Intersect(left, right);
        }



        //??
        public override Expression Visit(DbRefKeyExpression expression)
        {
            throw new NotImplementedException();
        }

        //??
        public override Expression Visit(DbEntityRefExpression expression)
        {
            throw new NotImplementedException();
        }

        //tamas-zsolt
        public override Expression Visit(DbFunctionExpression expression)
        {
            Expression[] arguments = new Expression[expression.Arguments.Count];

            for (int i = 0; i < expression.Arguments.Count; i++)
			{
			    arguments[i] = this.Visit(expression.Arguments[i]);
			}

            return this.functionMapper.CreateMethodCall( expression.Function, arguments);
        }

        //zsolt
        public override Expression Visit(DbExceptExpression expression)
        {
            Expression left = this.Visit(expression.Left);
            Expression right = this.Visit(expression.Right);

            return queryMethodExpressionBuilder.Except(left, right);
        }

        //tamas
        public override Expression Visit(DbElementExpression expression)
        {
            Expression source = this.Visit(expression.Argument);
            Expression single = queryMethodExpressionBuilder.FirstOrDefault(source);
            Expression result  = Expression.Property(single, single.Type.GetProperties()[0]);

            return result;
        }


        //tamas
        public override Expression Visit(DbDistinctExpression expression)
        {
            return queryMethodExpressionBuilder.Distinct(this.Visit(expression.Argument));
        }

        //zsolt
        public override Expression Visit(DbDerefExpression expression)
        {
            throw new NotImplementedException();
        }

        //zsolt
        //BMK CrossJoin
        public override Expression Visit(DbCrossJoinExpression expression)
        {
            List<Expression> inputExpressions = new List<Expression>();

            foreach (var i in expression.Inputs)
            {
                inputExpressions.Add(this.Visit(i.Expression));
            }

            Expression last = inputExpressions[0];
            // Itt mindig 2 volt, lehet nem lesz tobb soha, csak kevesebb
            for (int i = 1;i < inputExpressions.Count;i++)
            {
                last = queryMethodExpressionBuilder.SelectMany(last, inputExpressions[i],
                    expression.Inputs[i - 1].VariableName,
                    expression.Inputs[i].VariableName);
            }
            return last;
        }

        //tamas
        public override Expression Visit(DbConstantExpression expression)
        {
            return Expression.Constant(expression.Value, typeConverter.Convert(expression.ResultType));
        }

        //tamas
        public override Expression Visit(DbComparisonExpression expression)
        {
            Expression left = this.Visit(expression.Left);
            Expression right = this.Visit(expression.Right);

            ExpressionHelper.TryUnifyValueTypes(ref left, ref right);

            switch (expression.ExpressionKind)
            {
                case DbExpressionKind.Equals:
                    return Expression.Equal(left, right);

                case DbExpressionKind.NotEquals:
                    return Expression.NotEqual(left, right);

                case DbExpressionKind.GreaterThan:
                    return Expression.GreaterThan(left, right);

                case DbExpressionKind.GreaterThanOrEquals:
                    return Expression.GreaterThanOrEqual(left, right);

                case DbExpressionKind.LessThan:
                    return Expression.LessThan(left, right);

                case DbExpressionKind.LessThanOrEquals:
                    return Expression.LessThanOrEqual(left, right);
            }

            throw new InvalidOperationException("The ExpressionKind cannot be " + expression.ExpressionKind.ToString());
        }

        //tamas
        public override Expression Visit(DbCastExpression expression)
        {
            return Expression.Convert(this.Visit(expression.Argument), typeConverter.Convert(expression.ResultType));
        }

        //zsolt
        public override Expression Visit(DbCaseExpression expression)
        {
            List<Expression> cases = new List<Expression>() { this.Visit(expression.Else) };

            for (int i = expression.When.Count - 1;i >= 0;i--)
            {
                cases.Add(
                    Expression.Condition(
                        this.Visit(expression.When[i]), 
                        this.Visit(expression.Then[i]), 
                        cases.Last()));
            }

            return cases.Last();
        }

        //tamas
        public override Expression Visit(DbArithmeticExpression expression)
        {
            Expression[] args = new Expression[expression.Arguments.Count];

            for (int i = 0;i < expression.Arguments.Count;i++)
            {
                args[i] = this.Visit(expression.Arguments[i]);
            }

            //UnaryMinus has single argument, avoid it
            if (args.Length == 2)
            {
                ExpressionHelper.TryUnifyValueTypes(ref args[0], ref args[1]);
            }

            switch (expression.ExpressionKind)
            {
                case DbExpressionKind.Plus:
                    return Expression.Add(args[0], args[1]);

                case DbExpressionKind.Minus:
                    return Expression.Subtract(args[0], args[1]);

                case DbExpressionKind.Multiply:
                    return Expression.Multiply(args[0], args[1]);

                case DbExpressionKind.Divide:
                    return Expression.Divide(args[0], args[1]);

                case DbExpressionKind.Modulo:
                    return Expression.Modulo(args[0], args[1]);

                case DbExpressionKind.UnaryMinus:
                    return Expression.Negate(args[0]);
            }

            throw new InvalidOperationException("The ExpressionKind cannot be " + expression.ExpressionKind.ToString());
        }

        //zsolt
        public override Expression Visit(DbApplyExpression expression)
        {
            throw new NotImplementedException();
        }




    }
}
