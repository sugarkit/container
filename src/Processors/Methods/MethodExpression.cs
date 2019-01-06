﻿using System.Linq.Expressions;
using System.Reflection;
using Unity.Builder;

namespace Unity.Processors
{
    public partial class MethodProcessor
    {
        #region Building Expression

        protected override Expression ExpressionFromMemberInfo(MethodInfo info)
        {
            ValidateMethod(info);

            return Expression.Call(Expression.Convert(BuilderContextExpression.Existing, info.DeclaringType),
                info, CreateParameterExpressions(info.GetParameters()));
        }

        protected override Expression ExpressionFromMemberInfo(MethodInfo info, object[] resolvers)
        {
            ValidateMethod(info);

            return Expression.Call(Expression.Convert(BuilderContextExpression.Existing, info.DeclaringType),
                           info, CreateParameterExpressions(info.GetParameters(), resolvers));
        }

        #endregion
    }
}