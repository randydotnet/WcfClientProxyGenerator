﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;

namespace WcfClientProxyGenerator
{
    public class Generator<TServiceInterface>
        where TServiceInterface : class
    {
        public TServiceInterface Generate(Binding binding, EndpointAddress endpointAddress)
        {
            var assemblyName = new AssemblyName("temp");
            var appDomain = System.Threading.Thread.GetDomain();
            var assemblyBuilder = appDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name);

            var typeBuilder = moduleBuilder.DefineType(
                "Generated",
                TypeAttributes.Public | TypeAttributes.Class,
                typeof(ClientBase<TServiceInterface>));

            typeBuilder.AddInterfaceImplementation(typeof(TServiceInterface));

            GenerateConstructor(typeBuilder);

            var serviceMethods = typeof(TServiceInterface)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(t => t.GetCustomAttribute<OperationContractAttribute>() != null);

            foreach (var serviceMethod in serviceMethods)
            {
                GenerateMethod(serviceMethod, typeBuilder);
            }

            Type generatedType = typeBuilder.CreateType();
            var inst = Activator.CreateInstance(generatedType, new object[] { binding, endpointAddress }) as TServiceInterface;

            return inst;
        }

        private void GenerateConstructor(TypeBuilder typeBuilder)
        {
            var parameters = new[] { typeof(Binding), typeof(EndpointAddress) };
            var constructorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public, 
                CallingConventions.Standard, 
                parameters);

            var ilGenerator = constructorBuilder.GetILGenerator();

            ilGenerator.Emit(OpCodes.Ldarg_0); // this
            ilGenerator.Emit(OpCodes.Ldarg_1); // binding parameter
            ilGenerator.Emit(OpCodes.Ldarg_2); // endpoint address parameter

            var baseConstructor = typeof(ClientBase<TServiceInterface>)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, parameters, null);

            ilGenerator.Emit(OpCodes.Call, baseConstructor);
            ilGenerator.Emit(OpCodes.Ret);
        }

        private void GenerateMethod(MethodInfo methodInfo, TypeBuilder typeBuilder)
        {
            var parameterTypes = methodInfo.GetParameters().Select(m => m.ParameterType).ToArray();

            var methodBuilder = typeBuilder.DefineMethod(
                methodInfo.Name,
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                methodInfo.ReturnType,
                parameterTypes);

            //methodBuilder.SetReturnType(methodInfo.ReturnType);

            var ilGenerator = methodBuilder.GetILGenerator();
            ilGenerator.Emit(OpCodes.Ldarg_0);
            //ilGenerator.Emit(OpCodes.Ldarg_1);

            var channelProperty = typeof(ClientBase<TServiceInterface>)
                .GetMethod(
                    "get_Channel", 
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.GetProperty);

            ilGenerator.EmitCall(OpCodes.Call, channelProperty, null);

            ilGenerator.DeclareLocal(typeof(string));

            //ilGenerator.Emit(OpCodes.Ldloc_0);

            for (int i = 0; i < parameterTypes.Length; i++)
                ilGenerator.Emit(OpCodes.Ldarg, ((short) i + 1));

            ilGenerator.Emit(OpCodes.Call, methodInfo);

            ilGenerator.Emit(OpCodes.Stloc_0);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Ret);

            typeBuilder.DefineMethodOverride(methodBuilder, methodInfo);
        }

        private bool MethodHasReturnValue(MethodInfo methodInfo)
        {
            return methodInfo.ReturnType != typeof(void);
        }
    }

    public interface ITest
    {
        string Get(string arg);
    }

    public class TestImpl : Caller, ITest
    {
        public TestImpl(string c)
            : base(c)
        {}

        public string Get(string arg)
        {
            return base.Get(arg);
        }
    }

    public class Caller
    {
        private string _endpointConfigurationName;

        protected Caller(string endpointConfigurationName)
        {
            _endpointConfigurationName = endpointConfigurationName;
        }

        public string Get(string arg)
        {
            return _endpointConfigurationName + ": " + arg;
        }
    }
}
