// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic.Utils;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace System.Linq.Expressions
{
    internal sealed class DebugViewWriter : ExpressionVisitor
    {
        [Flags]
        private enum Flow
        {
            None,
            Space,
            NewLine,

            Break = 0x8000      // newline if column > MaxColumn
        };

        private const int Tab = 4;
        private const int MaxColumn = 120;

        private readonly TextWriter _out;
        private int _column;

        private int _depth;
        private Flow _flow;

        // All the unique lambda expressions in the ET, will be used for displaying all
        // the lambda definitions.
        private Queue<LambdaExpression>? _lambdas;

        // Associate every unique anonymous LambdaExpression in the tree with an integer.
        // The id is used to create a name for the anonymous lambda.
        //
        private Dictionary<LambdaExpression, int>? _lambdaIds;

        // Associate every unique anonymous parameter or variable in the tree with an integer.
        // The id is used to create a name for the anonymous parameter or variable.
        //
        private Dictionary<ParameterExpression, int>? _paramIds;

        // Associate every unique anonymous LabelTarget in the tree with an integer.
        // The id is used to create a name for the anonymous LabelTarget.
        //
        private Dictionary<LabelTarget, int>? _labelIds;

        private DebugViewWriter(TextWriter file)
        {
            _out = file;
        }

        private int Depth => _depth;

        private void Indent()
        {
            _depth += Tab;
        }

        private void Dedent()
        {
            _depth -= Tab;
        }

        private void NewLine()
        {
            _flow = Flow.NewLine;
        }

        private static int GetId<T>(T e, ref Dictionary<T, int>? ids) where T : notnull
        {
            if (ids == null)
            {
                ids = new Dictionary<T, int>();
                ids.Add(e, 1);
                return 1;
            }
            else
            {
                int id;
                if (!ids.TryGetValue(e, out id))
                {
                    // e is met the first time
                    id = ids.Count + 1;
                    ids.Add(e, id);
                }
                return id;
            }
        }

        private int GetLambdaId(LambdaExpression le)
        {
            Debug.Assert(string.IsNullOrEmpty(le.Name));
            return GetId(le, ref _lambdaIds);
        }

        private int GetParamId(ParameterExpression p)
        {
            Debug.Assert(string.IsNullOrEmpty(p.Name));
            return GetId(p, ref _paramIds);
        }

        private int GetLabelTargetId(LabelTarget target)
        {
            Debug.Assert(string.IsNullOrEmpty(target.Name));
            return GetId(target, ref _labelIds);
        }

        /// <summary>
        /// Write out the given AST
        /// </summary>
        internal static void WriteTo(Expression node, TextWriter writer)
        {
            Debug.Assert(node != null);
            Debug.Assert(writer != null);

            new DebugViewWriter(writer).WriteTo(node);
        }

        private void WriteTo(Expression node)
        {
            if (node is LambdaExpression lambda)
            {
                WriteLambda(lambda);
            }
            else
            {
                Visit(node);
            }

            //
            // Output all lambda expression definitions.
            // in the order of their appearances in the tree.
            //
            while (_lambdas != null && _lambdas.Count > 0)
            {
                WriteLine();
                WriteLine();
                WriteLambda(_lambdas.Dequeue());
            }
        }

        #region The printing code

        private void Out(string s)
        {
            Out(Flow.None, s, Flow.None);
        }

        private void Out(Flow before, string s)
        {
            Out(before, s, Flow.None);
        }

        private void Out(string s, Flow after)
        {
            Out(Flow.None, s, after);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Out(Flow before, string s, Flow after)
        {
            switch (GetFlow(before))
            {
                case Flow.None:
                    break;
                case Flow.Space:
                    Write(" ");
                    break;
                case Flow.NewLine:
                    WriteLine();
                    Write(new string(' ', Depth));
                    break;
            }
            Write(s);
            _flow = after;
        }

        private void WriteLine()
        {
            _out.WriteLine();
            _column = 0;
        }

        private void Write(string s)
        {
            _out.Write(s);
            _column += s.Length;
        }

        private Flow GetFlow(Flow flow)
        {
            Flow last = CheckBreak(_flow);
            flow = CheckBreak(flow);

            // Get the biggest flow that is requested None < Space < NewLine
            return (Flow)System.Math.Max((int)last, (int)flow);
        }

        private Flow CheckBreak(Flow flow)
        {
            if ((flow & Flow.Break) != 0)
            {
                if (_column > (MaxColumn + Depth))
                {
                    flow = Flow.NewLine;
                }
                else
                {
                    flow &= ~Flow.Break;
                }
            }
            return flow;
        }

        #endregion

        #region The AST Output

        private void VisitExpressions<T>(char open, IReadOnlyList<T> expressions) where T : Expression
        {
            VisitExpressions<T>(open, ',', expressions);
        }

        private void VisitExpressions<T>(char open, char separator, IReadOnlyList<T> expressions) where T : Expression
        {
            VisitExpressions(open, separator, expressions, e => Visit(e));
        }

        private void VisitDeclarations(IReadOnlyList<ParameterExpression> expressions)
        {
            VisitExpressions('(', ',', expressions, variable =>
            {
                Out(variable.Type.ToString());
                if (variable.IsByRef)
                {
                    Out("&");
                }
                Out(" ");
                VisitParameter(variable);
            });
        }

        private void VisitExpressions<T>(char open, char separator, IReadOnlyList<T> expressions, Action<T> visit)
        {
            Out(open.ToString());

            if (expressions != null)
            {
                Indent();
                bool isFirst = true;
                foreach (T e in expressions)
                {
                    if (isFirst)
                    {
                        if (open == '{' || expressions.Count > 1)
                        {
                            NewLine();
                        }
                        isFirst = false;
                    }
                    else
                    {
                        Out(separator.ToString(), Flow.NewLine);
                    }
                    visit(e);
                }
                Dedent();
            }

            char close = open switch
            {
                '(' => ')',
                '{' => '}',
                '[' => ']',
                _ => throw ContractUtils.Unreachable,
            };

            if (open == '{')
            {
                NewLine();
            }
            Out(close.ToString(), Flow.Break);
        }

        protected internal override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.ArrayIndex)
            {
                ParenthesizedVisit(node, node.Left);
                Out("[");
                Visit(node.Right);
                Out("]");
            }
            else
            {
                bool parenthesizeLeft = NeedsParentheses(node, node.Left);
                bool parenthesizeRight = NeedsParentheses(node, node.Right);

                string op;
                Flow beforeOp = Flow.Space;
                switch (node.NodeType)
                {
                    case ExpressionType.Assign: op = "="; break;
                    case ExpressionType.Equal: op = "=="; break;
                    case ExpressionType.NotEqual: op = "!="; break;
                    case ExpressionType.AndAlso: op = "&&"; beforeOp = Flow.Break | Flow.Space; break;
                    case ExpressionType.OrElse: op = "||"; beforeOp = Flow.Break | Flow.Space; break;
                    case ExpressionType.GreaterThan: op = ">"; break;
                    case ExpressionType.LessThan: op = "<"; break;
                    case ExpressionType.GreaterThanOrEqual: op = ">="; break;
                    case ExpressionType.LessThanOrEqual: op = "<="; break;
                    case ExpressionType.Add: op = "+"; break;
                    case ExpressionType.AddAssign: op = "+="; break;
                    case ExpressionType.AddAssignChecked: op = "#+="; break;
                    case ExpressionType.AddChecked: op = "#+"; break;
                    case ExpressionType.Subtract: op = "-"; break;
                    case ExpressionType.SubtractAssign: op = "-="; break;
                    case ExpressionType.SubtractAssignChecked: op = "#-="; break;
                    case ExpressionType.SubtractChecked: op = "#-"; break;
                    case ExpressionType.Divide: op = "/"; break;
                    case ExpressionType.DivideAssign: op = "/="; break;
                    case ExpressionType.Modulo: op = "%"; break;
                    case ExpressionType.ModuloAssign: op = "%="; break;
                    case ExpressionType.Multiply: op = "*"; break;
                    case ExpressionType.MultiplyAssign: op = "*="; break;
                    case ExpressionType.MultiplyAssignChecked: op = "#*="; break;
                    case ExpressionType.MultiplyChecked: op = "#*"; break;
                    case ExpressionType.LeftShift: op = "<<"; break;
                    case ExpressionType.LeftShiftAssign: op = "<<="; break;
                    case ExpressionType.RightShift: op = ">>"; break;
                    case ExpressionType.RightShiftAssign: op = ">>="; break;
                    case ExpressionType.And: op = "&"; break;
                    case ExpressionType.AndAssign: op = "&="; break;
                    case ExpressionType.Or: op = "|"; break;
                    case ExpressionType.OrAssign: op = "|="; break;
                    case ExpressionType.ExclusiveOr: op = "^"; break;
                    case ExpressionType.ExclusiveOrAssign: op = "^="; break;
                    case ExpressionType.Power: op = "**"; break;
                    case ExpressionType.PowerAssign: op = "**="; break;
                    case ExpressionType.Coalesce: op = "??"; break;

                    default:
                        throw new InvalidOperationException();
                }

                if (parenthesizeLeft)
                {
                    Out("(", Flow.None);
                }

                Visit(node.Left);
                if (parenthesizeLeft)
                {
                    Out(Flow.None, ")", Flow.Break);
                }

                Out(beforeOp, op, Flow.Space | Flow.Break);

                if (parenthesizeRight)
                {
                    Out("(", Flow.None);
                }
                Visit(node.Right);
                if (parenthesizeRight)
                {
                    Out(Flow.None, ")", Flow.Break);
                }
            }
            return node;
        }

        protected internal override Expression VisitParameter(ParameterExpression node)
        {
            // Have '$' for the DebugView of ParameterExpressions
            Out("$");
            if (string.IsNullOrEmpty(node.Name))
            {
                // If no name if provided, generate a name as $var1, $var2.
                // No guarantee for not having name conflicts with user provided variable names.
                //
                int id = GetParamId(node);
                Out("var" + id);
            }
            else
            {
                Out(GetDisplayName(node.Name));
            }
            return node;
        }

        protected internal override Expression VisitLambda<T>(Expression<T> node)
        {
            Out($".Lambda {GetLambdaName(node)}<{node.Type}>");

            if (_lambdas == null)
            {
                _lambdas = new Queue<LambdaExpression>();
            }

            // N^2 performance, for keeping the order of the lambdas.
            if (!_lambdas.Contains(node))
            {
                _lambdas.Enqueue(node);
            }

            return node;
        }

        private static bool IsSimpleExpression(Expression node)
        {
            var binary = node as BinaryExpression;
            if (binary != null)
            {
                return !(binary.Left is BinaryExpression || binary.Right is BinaryExpression);
            }

            return false;
        }

        protected internal override Expression VisitConditional(ConditionalExpression node)
        {
            if (IsSimpleExpression(node.Test))
            {
                Out(".If (");
                Visit(node.Test);
                Out(") {", Flow.NewLine);
            }
            else
            {
                Out(".If (", Flow.NewLine);
                Indent();
                Visit(node.Test);
                Dedent();
                Out(Flow.NewLine, ") {", Flow.NewLine);
            }
            Indent();
            Visit(node.IfTrue);
            Dedent();
            Out(Flow.NewLine, "} .Else {", Flow.NewLine);
            Indent();
            Visit(node.IfFalse);
            Dedent();
            Out(Flow.NewLine, "}");
            return node;
        }

        protected internal override Expression VisitConstant(ConstantExpression node)
        {
            object? value = node.Value;

            if (value == null)
            {
                Out("null");
            }
            else if ((value is string) && node.Type == typeof(string))
            {
                Out($"\"{value}\"");
            }
            else if ((value is char) && node.Type == typeof(char))
            {
                Out($"'{value}'");
            }
            else if ((value is int) && node.Type == typeof(int)
              || (value is bool) && node.Type == typeof(bool))
            {
                Out(value.ToString()!);
            }
            else
            {
                string? suffix = GetConstantValueSuffix(node.Type);
                if (suffix != null)
                {
                    Out(value.ToString()!);
                    Out(suffix);
                }
                else
                {
                    Out($".Constant<{node.Type}>({value})");
                }
            }
            return node;
        }

        private static string? GetConstantValueSuffix(Type type)
        {
            if (type == typeof(uint))
            {
                return "U";
            }
            if (type == typeof(long))
            {
                return "L";
            }
            if (type == typeof(ulong))
            {
                return "UL";
            }
            if (type == typeof(double))
            {
                return "D";
            }
            if (type == typeof(float))
            {
                return "F";
            }
            if (type == typeof(decimal))
            {
                return "M";
            }
            return null;
        }

        protected internal override Expression VisitRuntimeVariables(RuntimeVariablesExpression node)
        {
            Out(".RuntimeVariables");
            VisitExpressions('(', node.Variables);
            return node;
        }

        // Prints ".instanceField" or "declaringType.staticField"
        private void OutMember(Expression node, Expression? instance, MemberInfo member)
        {
            if (instance != null)
            {
                ParenthesizedVisit(node, instance);
                Out("." + member.Name);
            }
            else
            {
                // For static members, include the type name
                Out(member.DeclaringType!.ToString() + "." + member.Name);
            }
        }

        protected internal override Expression VisitMember(MemberExpression node)
        {
            OutMember(node, node.Expression, node.Member);
            return node;
        }

        protected internal override Expression VisitInvocation(InvocationExpression node)
        {
            Out(".Invoke ");
            ParenthesizedVisit(node, node.Expression);
            VisitExpressions('(', node.Arguments);
            return node;
        }

        private static bool NeedsParentheses(Expression parent, Expression? child)
        {
            Debug.Assert(parent != null);
            if (child == null)
            {
                return false;
            }

            // Some nodes always have parentheses because of how they are
            // displayed, for example: ".Unbox(obj.Foo)"
            switch (parent.NodeType)
            {
                case ExpressionType.Increment:
                case ExpressionType.Decrement:
                case ExpressionType.IsTrue:
                case ExpressionType.IsFalse:
                case ExpressionType.Unbox:
                    return true;
            }

            int childOpPrec = GetOperatorPrecedence(child);
            int parentOpPrec = GetOperatorPrecedence(parent);

            if (childOpPrec == parentOpPrec)
            {
                // When parent op and child op has the same precedence,
                // we want to be a little conservative to have more clarity.
                // Parentheses are not needed if
                // 1) Both ops are &&, ||, &, |, or ^, all of them are the only
                // op that has the precedence.
                // 2) Parent op is + or *, e.g. x + (y - z) can be simplified to
                // x + y - z.
                // 3) Parent op is -, / or %, and the child is the left operand.
                // In this case, if left and right operand are the same, we don't
                // remove parenthesis, e.g. (x + y) - (x + y)
                //
                switch (parent.NodeType)
                {
                    case ExpressionType.AndAlso:
                    case ExpressionType.OrElse:
                    case ExpressionType.And:
                    case ExpressionType.Or:
                    case ExpressionType.ExclusiveOr:
                        // Since these ops are the only ones on their precedence,
                        // the child op must be the same.
                        Debug.Assert(child.NodeType == parent.NodeType);
                        // We remove the parenthesis, e.g. x && y && z
                        return false;
                    case ExpressionType.Add:
                    case ExpressionType.AddChecked:
                    case ExpressionType.Multiply:
                    case ExpressionType.MultiplyChecked:
                        return false;
                    case ExpressionType.Subtract:
                    case ExpressionType.SubtractChecked:
                    case ExpressionType.Divide:
                    case ExpressionType.Modulo:
                        BinaryExpression? binary = parent as BinaryExpression;
                        Debug.Assert(binary != null);
                        // Need to have parenthesis for the right operand.
                        return child == binary.Right;
                }
                return true;
            }

            // Special case: negate of a constant needs parentheses, to
            // disambiguate it from a negative constant.
            if (child != null && child.NodeType == ExpressionType.Constant &&
                (parent.NodeType == ExpressionType.Negate || parent.NodeType == ExpressionType.NegateChecked))
            {
                return true;
            }

            // If the parent op has higher precedence, need parentheses for the child.
            return childOpPrec < parentOpPrec;
        }

        // the greater the higher
        private static int GetOperatorPrecedence(Expression node)
        {
            // Roughly matches C# operator precedence, with some additional
            // operators. Also things which are not binary/unary expressions,
            // such as conditional and type testing, don't use this mechanism.
            switch (node.NodeType)
            {
                // Assignment
                case ExpressionType.Assign:
                case ExpressionType.ExclusiveOrAssign:
                case ExpressionType.AddAssign:
                case ExpressionType.AddAssignChecked:
                case ExpressionType.SubtractAssign:
                case ExpressionType.SubtractAssignChecked:
                case ExpressionType.DivideAssign:
                case ExpressionType.ModuloAssign:
                case ExpressionType.MultiplyAssign:
                case ExpressionType.MultiplyAssignChecked:
                case ExpressionType.LeftShiftAssign:
                case ExpressionType.RightShiftAssign:
                case ExpressionType.AndAssign:
                case ExpressionType.OrAssign:
                case ExpressionType.PowerAssign:
                case ExpressionType.Coalesce:
                    return 1;

                // Conditional (?:) would go here

                // Conditional OR
                case ExpressionType.OrElse:
                    return 2;

                // Conditional AND
                case ExpressionType.AndAlso:
                    return 3;

                // Logical OR
                case ExpressionType.Or:
                    return 4;

                // Logical XOR
                case ExpressionType.ExclusiveOr:
                    return 5;

                // Logical AND
                case ExpressionType.And:
                    return 6;

                // Equality
                case ExpressionType.Equal:
                case ExpressionType.NotEqual:
                    return 7;

                // Relational, type testing
                case ExpressionType.GreaterThan:
                case ExpressionType.LessThan:
                case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.LessThanOrEqual:
                case ExpressionType.TypeAs:
                case ExpressionType.TypeIs:
                case ExpressionType.TypeEqual:
                    return 8;

                // Shift
                case ExpressionType.LeftShift:
                case ExpressionType.RightShift:
                    return 9;

                // Additive
                case ExpressionType.Add:
                case ExpressionType.AddChecked:
                case ExpressionType.Subtract:
                case ExpressionType.SubtractChecked:
                    return 10;

                // Multiplicative
                case ExpressionType.Divide:
                case ExpressionType.Modulo:
                case ExpressionType.Multiply:
                case ExpressionType.MultiplyChecked:
                    return 11;

                // Unary
                case ExpressionType.Negate:
                case ExpressionType.NegateChecked:
                case ExpressionType.UnaryPlus:
                case ExpressionType.Not:
                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                case ExpressionType.PreIncrementAssign:
                case ExpressionType.PreDecrementAssign:
                case ExpressionType.OnesComplement:
                case ExpressionType.Increment:
                case ExpressionType.Decrement:
                case ExpressionType.IsTrue:
                case ExpressionType.IsFalse:
                case ExpressionType.Unbox:
                case ExpressionType.Throw:
                    return 12;

                // Power, which is not in C#
                // But VB/Python/Ruby put it here, above unary.
                case ExpressionType.Power:
                    return 13;

                // Primary, which includes all other node types:
                //   member access, calls, indexing, new.
                case ExpressionType.PostIncrementAssign:
                case ExpressionType.PostDecrementAssign:
                default:
                    return 14;

                // These aren't expressions, so never need parentheses:
                //   constants, variables
                case ExpressionType.Constant:
                case ExpressionType.Parameter:
                    return 15;
            }
        }

        private void ParenthesizedVisit(Expression parent, Expression? nodeToVisit)
        {
            if (NeedsParentheses(parent, nodeToVisit))
            {
                Out("(");
                Visit(nodeToVisit);
                Out(")");
            }
            else
            {
                Visit(nodeToVisit);
            }
        }

        protected internal override Expression VisitMethodCall(MethodCallExpression node)
        {
            Out(".Call ");
            if (node.Object != null)
            {
                ParenthesizedVisit(node, node.Object);
            }
            else if (node.Method.DeclaringType != null)
            {
                Out(node.Method.DeclaringType.ToString());
            }
            else
            {
                Out("<UnknownType>");
            }
            Out(".");
            Out(node.Method.Name);
            VisitExpressions('(', node.Arguments);
            return node;
        }

        protected internal override Expression VisitNewArray(NewArrayExpression node)
        {
            if (node.NodeType == ExpressionType.NewArrayBounds)
            {
                // .NewArray MyType[expr1, expr2]
                Out(".NewArray " + node.Type.GetElementType()!.ToString());
                VisitExpressions('[', node.Expressions);
            }
            else
            {
                // .NewArray MyType {expr1, expr2}
                Out(".NewArray " + node.Type.ToString(), Flow.Space);
                VisitExpressions('{', node.Expressions);
            }
            return node;
        }

        protected internal override Expression VisitNew(NewExpression node)
        {
            Out(".New " + node.Type.ToString());
            VisitExpressions('(', node.Arguments);
            return node;
        }

        protected override ElementInit VisitElementInit(ElementInit node)
        {
            if (node.Arguments.Count == 1)
            {
                Visit(node.Arguments[0]);
            }
            else
            {
                VisitExpressions('{', node.Arguments);
            }
            return node;
        }

        protected internal override Expression VisitListInit(ListInitExpression node)
        {
            Visit(node.NewExpression);
            VisitExpressions('{', ',', node.Initializers, e => VisitElementInit(e));
            return node;
        }

        protected override MemberAssignment VisitMemberAssignment(MemberAssignment assignment)
        {
            Out(assignment.Member.Name);
            Out(Flow.Space, "=", Flow.Space);
            Visit(assignment.Expression);
            return assignment;
        }

        protected override MemberListBinding VisitMemberListBinding(MemberListBinding binding)
        {
            Out(binding.Member.Name);
            Out(Flow.Space, "=", Flow.Space);
            VisitExpressions('{', ',', binding.Initializers, e => VisitElementInit(e));
            return binding;
        }

        protected override MemberMemberBinding VisitMemberMemberBinding(MemberMemberBinding binding)
        {
            Out(binding.Member.Name);
            Out(Flow.Space, "=", Flow.Space);
            VisitExpressions('{', ',', binding.Bindings, e => VisitMemberBinding(e));
            return binding;
        }

        protected internal override Expression VisitMemberInit(MemberInitExpression node)
        {
            Visit(node.NewExpression);
            VisitExpressions('{', ',', node.Bindings, e => VisitMemberBinding(e));
            return node;
        }

        protected internal override Expression VisitTypeBinary(TypeBinaryExpression node)
        {
            ParenthesizedVisit(node, node.Expression);
            switch (node.NodeType)
            {
                case ExpressionType.TypeIs:
                    Out(Flow.Space, ".Is", Flow.Space);
                    break;
                case ExpressionType.TypeEqual:
                    Out(Flow.Space, ".TypeEqual", Flow.Space);
                    break;
            }
            Out(node.TypeOperand.ToString());
            return node;
        }

        protected internal override Expression VisitUnary(UnaryExpression node)
        {
            switch (node.NodeType)
            {
                case ExpressionType.Convert:
                    Out("(" + node.Type.ToString() + ")");
                    break;
                case ExpressionType.ConvertChecked:
                    Out("#(" + node.Type.ToString() + ")");
                    break;
                case ExpressionType.TypeAs:
                    break;
                case ExpressionType.Not:
                    Out(node.Type == typeof(bool) ? "!" : "~");
                    break;
                case ExpressionType.OnesComplement:
                    Out("~");
                    break;
                case ExpressionType.Negate:
                    Out("-");
                    break;
                case ExpressionType.NegateChecked:
                    Out("#-");
                    break;
                case ExpressionType.UnaryPlus:
                    Out("+");
                    break;
                case ExpressionType.ArrayLength:
                    break;
                case ExpressionType.Quote:
                    Out("'");
                    break;
                case ExpressionType.Throw:
                    if (node.Operand == null)
                    {
                        Out(".Rethrow");
                    }
                    else
                    {
                        Out(".Throw", Flow.Space);
                    }
                    break;
                case ExpressionType.IsFalse:
                    Out(".IsFalse");
                    break;
                case ExpressionType.IsTrue:
                    Out(".IsTrue");
                    break;
                case ExpressionType.Decrement:
                    Out(".Decrement");
                    break;
                case ExpressionType.Increment:
                    Out(".Increment");
                    break;
                case ExpressionType.PreDecrementAssign:
                    Out("--");
                    break;
                case ExpressionType.PreIncrementAssign:
                    Out("++");
                    break;
                case ExpressionType.Unbox:
                    Out(".Unbox");
                    break;
            }

            ParenthesizedVisit(node, node.Operand);

            switch (node.NodeType)
            {
                case ExpressionType.TypeAs:
                    Out(Flow.Space, ".As", Flow.Space | Flow.Break);
                    Out(node.Type.ToString());
                    break;

                case ExpressionType.ArrayLength:
                    Out(".Length");
                    break;

                case ExpressionType.PostDecrementAssign:
                    Out("--");
                    break;

                case ExpressionType.PostIncrementAssign:
                    Out("++");
                    break;
            }
            return node;
        }

        protected internal override Expression VisitBlock(BlockExpression node)
        {
            Out(".Block");

            // Display <type> if the type of the BlockExpression is different from the
            // last expression's type in the block.
            if (node.Type != node.GetExpression(node.ExpressionCount - 1).Type)
            {
                Out($"<{node.Type}>");
            }

            VisitDeclarations(node.Variables);
            Out(" ");
            // Use ; to separate expressions in the block
            VisitExpressions('{', ';', node.Expressions);

            return node;
        }

        protected internal override Expression VisitDefault(DefaultExpression node)
        {
            Out(".Default(" + node.Type.ToString() + ")");
            return node;
        }

        protected internal override Expression VisitLabel(LabelExpression node)
        {
            Out(".Label", Flow.NewLine);
            Indent();
            Visit(node.DefaultValue);
            Dedent();
            NewLine();
            DumpLabel(node.Target);
            return node;
        }

        protected internal override Expression VisitGoto(GotoExpression node)
        {
            Out("." + node.Kind.ToString(), Flow.Space);
            Out(GetLabelTargetName(node.Target), Flow.Space);
            Out("{", Flow.Space);
            Visit(node.Value);
            Out(Flow.Space, "}");
            return node;
        }

        protected internal override Expression VisitLoop(LoopExpression node)
        {
            Out(".Loop", Flow.Space);
            if (node.ContinueLabel != null)
            {
                DumpLabel(node.ContinueLabel);
            }
            Out(" {", Flow.NewLine);
            Indent();
            Visit(node.Body);
            Dedent();
            Out(Flow.NewLine, "}");
            if (node.BreakLabel != null)
            {
                Out("", Flow.NewLine);
                DumpLabel(node.BreakLabel);
            }
            return node;
        }

        protected override SwitchCase VisitSwitchCase(SwitchCase node)
        {
            foreach (Expression test in node.TestValues)
            {
                Out(".Case (");
                Visit(test);
                Out("):", Flow.NewLine);
            }
            Indent(); Indent();
            Visit(node.Body);
            Dedent(); Dedent();
            NewLine();
            return node;
        }

        protected internal override Expression VisitSwitch(SwitchExpression node)
        {
            Out(".Switch ");
            Out("(");
            Visit(node.SwitchValue);
            Out(") {", Flow.NewLine);
            Visit(node.Cases, VisitSwitchCase);
            if (node.DefaultBody != null)
            {
                Out(".Default:", Flow.NewLine);
                Indent(); Indent();
                Visit(node.DefaultBody);
                Dedent(); Dedent();
                NewLine();
            }
            Out("}");
            return node;
        }

        protected override CatchBlock VisitCatchBlock(CatchBlock node)
        {
            Out(Flow.NewLine, "} .Catch (" + node.Test.ToString());
            if (node.Variable != null)
            {
                Out(Flow.Space, "");
                VisitParameter(node.Variable);
            }
            if (node.Filter != null)
            {
                Out(") .If (", Flow.Break);
                Visit(node.Filter);
            }
            Out(") {", Flow.NewLine);
            Indent();
            Visit(node.Body);
            Dedent();
            return node;
        }

        protected internal override Expression VisitTry(TryExpression node)
        {
            Out(".Try {", Flow.NewLine);
            Indent();
            Visit(node.Body);
            Dedent();
            Visit(node.Handlers, VisitCatchBlock);
            if (node.Finally != null)
            {
                Out(Flow.NewLine, "} .Finally {", Flow.NewLine);
                Indent();
                Visit(node.Finally);
                Dedent();
            }
            else if (node.Fault != null)
            {
                Out(Flow.NewLine, "} .Fault {", Flow.NewLine);
                Indent();
                Visit(node.Fault);
                Dedent();
            }

            Out(Flow.NewLine, "}");
            return node;
        }

        protected internal override Expression VisitIndex(IndexExpression node)
        {
            if (node.Indexer != null)
            {
                OutMember(node, node.Object, node.Indexer);
            }
            else
            {
                ParenthesizedVisit(node, node.Object);
            }

            VisitExpressions('[', node.Arguments);
            return node;
        }

        protected internal override Expression VisitExtension(Expression node)
        {
            Out($".Extension<{node.GetType()}>");

            if (node.CanReduce)
            {
                Out(Flow.Space, "{", Flow.NewLine);
                Indent();
                Visit(node.Reduce());
                Dedent();
                Out(Flow.NewLine, "}");
            }

            return node;
        }

        protected internal override Expression VisitDebugInfo(DebugInfoExpression node)
        {
            Out($".DebugInfo({node.Document.FileName}: {node.StartLine}, {node.StartColumn} - {node.EndLine}, {node.EndColumn})");
            return node;
        }


        private void DumpLabel(LabelTarget target)
        {
            Out($".LabelTarget {GetLabelTargetName(target)}:");
        }

        private string GetLabelTargetName(LabelTarget target)
        {
            if (string.IsNullOrEmpty(target.Name))
            {
                // Create the label target name as #Label1, #Label2, etc.
                return "#Label" + GetLabelTargetId(target);
            }
            else
            {
                return GetDisplayName(target.Name);
            }
        }

        private void WriteLambda(LambdaExpression lambda)
        {
            Out($".Lambda {GetLambdaName(lambda)}<{lambda.Type}>");

            VisitDeclarations(lambda.Parameters);

            Out(Flow.Space, "{", Flow.NewLine);
            Indent();
            Visit(lambda.Body);
            Dedent();
            Out(Flow.NewLine, "}");
        }

        private string GetLambdaName(LambdaExpression lambda)
        {
            if (string.IsNullOrEmpty(lambda.Name))
            {
                return "#Lambda" + GetLambdaId(lambda);
            }
            return GetDisplayName(lambda.Name);
        }

        /// <summary>
        /// Return true if the input string contains any whitespace character.
        /// Otherwise false.
        /// </summary>
        private static bool ContainsWhiteSpace(string name)
        {
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c))
                {
                    return true;
                }
            }
            return false;
        }

        private static string QuoteName(string name)
        {
            return "'" + name + "'";
        }

        private static string GetDisplayName(string name)
        {
            if (ContainsWhiteSpace(name))
            {
                // if name has whitespace in it, quote it
                return QuoteName(name);
            }
            else
            {
                return name;
            }
        }

        #endregion
    }
}
