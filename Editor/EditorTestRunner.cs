
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public static class EditorTestRunner
{
    public static List<string> IgnoredTargetAssemblies { get; set; } = new List<string>() { "UnityEngine", "mscorlib", "System" };

    private struct EditorTest
    {
        public readonly string Key;
        public readonly Action Test;
        public readonly List<Action> Setups;
        public readonly List<Action> Teardowns;

        public EditorTest(string title, Action test, List<Action> setups, List<Action> teardowns) 
        {
            Key = title;
            Test = test;
            Setups = setups;
            Teardowns = teardowns;
        }
    }

    private static Dictionary<string, List<EditorTest>> RegisteredTests { get; set; }

    public static void RunAllTests()
    {
        RegisterTests();

        RunTests();
    }

    private static void RunTests()
    {
    }

    private static void RegisterTests()
    {
        RegisteredTests.Clear();

        foreach (var testClassType in GetTestClasses())
        {
            var test = Activator.CreateInstance(testClassType);
            var methods = testClassType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

            //=> Create actions for all setups and teardowns.
            List<Action> setups = new List<Action>();
            List<Action> teardowns = new List<Action>();

            foreach (var method in methods)
            {
                // Create a delegate for each setup attribute.
                var setup = method.GetCustomAttribute<SetUpAttribute>(true);
                if (setup != null)
                {
                    setups.Add((Action)Delegate.CreateDelegate(typeof(Action), test, method));
                }

                // Create a delegate for each teardown attribute.
                var teardown = method.GetCustomAttribute<TearDownAttribute>(true);
                if (teardown != null)
                {
                    teardowns.Add((Action)Delegate.CreateDelegate(typeof(Action), test, method));
                }
            }

            //=> Create actions for all tests.
            foreach (var method in methods)
            {
                var standardTest = method.GetCustomAttribute<NUnit.Framework.TestAttribute>(true);

                if (standardTest is null)
                    continue;

                // The test will be ran only once.
                if (method.GetParameters().Length == 0 && method.ReturnType == typeof(void))
                {
                    var invoke = (Action)Delegate.CreateDelegate(typeof(Action), test, method);
                    RegisterTest(invoke.Target.GetType().Name, invoke.Method.Name, invoke, setups, teardowns);
                }
                // The test will be ran multiple times with different data sets.
                else
                {
                    var testDatas = GetTestData(method);

                    if (testDatas.Count == 0)
                        throw new Exception($"{testClassType.Name}.{method.Name} not supported (multiple parameter without TestCase or return type is invalid).");

                    foreach (var dataSet in testDatas)
                    {
                        Action invoke = null;

                        if (method.IsGenericMethod)
                        {
                            var method2 = InferGenericType(method, dataSet);
                            invoke = () => method2.Invoke(test, dataSet);
                        }
                        else
                        {
                            invoke = () => method.Invoke(test, dataSet);
                        }

                        var name = $"{method.Name}({string.Join(", ", dataSet.Select(x => x?.ToString() ?? "null"))})";
                        name = name.Replace(Char.MinValue, ' ').Replace(Char.MaxValue, ' ').Replace("<", "[").Replace(">", "]");
                        RegisterTest(test.GetType().Name, name, invoke, setups, teardowns);
                    }
                }
            }
        }
    }

    private static void RegisterTest(string group, string title, Action test, List<Action> setups, List<Action> teardowns)
    {
        if (!RegisteredTests.ContainsKey(group))
        {
            RegisteredTests[group] = new List<EditorTest>();
        }

        RegisteredTests[group].Add(new EditorTest(title, test, setups, teardowns));
    }

    /// <summary>
    /// See https://github.com/nunit/docs/wiki/TestCase-Attribute
    /// </summary>
    private static List<object[]> GetTestData(MethodInfo methodInfo)
    {
        List<object[]> testCases = new List<object[]>();

        var inlineData = methodInfo.GetCustomAttributes<NUnit.Framework.TestCaseAttribute>(true);
        foreach (var item in inlineData)
        {
            testCases.Add(item.Arguments);
        }

        foreach (var sourceAttribute in methodInfo.GetCustomAttributes<NUnit.Framework.TestCaseSourceAttribute>(true))
        {
            foreach (var sourceData in GetTestCaseSource(methodInfo, sourceAttribute.SourceType, sourceAttribute.SourceName, sourceAttribute.MethodParams))
            {
                if (sourceData is IEnumerable dataList)
                {
                    testCases.Add(dataList.OfType<object>().ToArray());
                }
            }
        }

        return testCases;
    }

    /// <summary>
    /// See https://github.com/nunit/docs/wiki/TestCaseSource-Attribute.
    /// </summary>
    private static IEnumerable GetTestCaseSource(MethodInfo method, Type sourceType, string sourceName, object[] methodParams)
    {
        Type type = sourceType ?? method.DeclaringType;

        MemberInfo[] member = type.GetMember(sourceName, BindingFlags.FlattenHierarchy | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        if (member.Length == 1)
        {
            MemberInfo memberInfo = member[0];

            if (memberInfo is FieldInfo fieldInfo)
            {
                if (!fieldInfo.IsStatic) throw new Exception("The sourceName specified on a TestCaseSourceAttribute must refer to a static field, property or method.");
                if (methodParams != null) throw new Exception("You have specified a data source field but also given a set of parameters. Fields cannot take parameters, please revise the 3rd parameter passed to the TestCaseSourceAttribute and either remove it or specify a method.");
                return ((IEnumerable)fieldInfo.GetValue(null));
            }
            else if (memberInfo is PropertyInfo propertyInfo)
            {
                if (!propertyInfo.GetGetMethod(nonPublic: true).IsStatic) throw new Exception("The sourceName specified on a TestCaseSourceAttribute must refer to a static field, property or method.");
                if (methodParams != null) throw new Exception("You have specified a data source field but also given a set of parameters. Fields cannot take parameters, please revise the 3rd parameter passed to the TestCaseSourceAttribute and either remove it or specify a method.");
                return ((IEnumerable)propertyInfo.GetValue(null, null));
            }
            else if (memberInfo is MethodInfo methodInfo)
            {
                if (!methodInfo.IsStatic) throw new Exception("The sourceName specified on a TestCaseSourceAttribute must refer to a static field, property or method.");
                if (methodParams != null && methodInfo.GetParameters().Length != methodParams.Length) throw new Exception("You have given the wrong number of arguments to the method in the TestCaseSourceAttribute, please check the number of parameters passed in the object is correct in the 3rd parameter for the TestCaseSourceAttribute and this matches the number of parameters in the target method and try again.");
                return ((IEnumerable)methodInfo.Invoke(null, methodParams));
            }
        }

        return null;
    }

    private static MethodInfo InferGenericType(MethodInfo methodInfo, object[] parameters)
    {
        var set = new HashSet<Type>();

        List<Type> genericParameters = new List<Type>();

        foreach (var item in methodInfo.GetParameters()
            .Where(x => x.ParameterType.IsGenericParameter)
            .Select((x, i) => new { x.ParameterType, i })
            .OrderBy(x => x.ParameterType.GenericParameterPosition))
        {
            if (set.Add(item.ParameterType))
            {
                genericParameters.Add(parameters[item.i].GetType());
            }
        }

        return methodInfo.MakeGenericMethod(genericParameters.ToArray());
    }

    private static IEnumerable<Type> GetTestClasses()
    {
        foreach (var assembly in GetFilteredAssemblies())
        {
            foreach (var item in assembly.GetTypes())
            {
                foreach (var method in item.GetMethods())
                {
                    // A class is a test class if it contains at least one test.
                    if (method.GetCustomAttribute<TestAttribute>(true) != null)
                    {
                        yield return item;
                        break;
                    }
                }
            }
        }
    }

    private static IEnumerable<Assembly> GetFilteredAssemblies()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = assembly.FullName;

            if (IgnoredTargetAssemblies.Any(assemblyName => n.StartsWith(assemblyName)))
                continue;

            yield return assembly;
        }
    }
}
