using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Practices.Unity;
using System.Linq;
using System;

namespace GitHub
{
    [TestClass]
    public class Issues
    {
        [TestMethod]    // https://github.com/unitycontainer/unity/issues/88
        public void InvalidCastExceptionWhenResolvingValueTypes_88()
        {
            using (var unityContainer = new UnityContainer())
            {
                unityContainer.RegisterInstance(true);
                unityContainer.RegisterInstance("true", true);
                unityContainer.RegisterInstance("false", false);

                var resolveAll = unityContainer.ResolveAll(typeof(bool));
                var arr = resolveAll.Select(o => o.ToString()).ToArray();
            }
        }

        [TestMethod]    // https://github.com/unitycontainer/unity/issues/54
        public void StackOverflowException_54()
        {
            using (IUnityContainer container = new UnityContainer())
            {
                container.RegisterType(typeof(ITestClass), typeof(TestClass));
                container.RegisterInstance(new TestClass());
                var instance = container.Resolve<ITestClass>(); //0
                Assert.IsNotNull(instance);
            }

            using (IUnityContainer container = new UnityContainer())
            {
                container.RegisterType(typeof(ITestClass), typeof(TestClass));
                container.RegisterType<TestClass>(new ContainerControlledLifetimeManager());

                try
                {
                    var instance = container.Resolve<ITestClass>(); //2
                    Assert.IsNull(instance, "Should threw an exception");
                }
                catch (ResolutionFailedException e)
                {
                    Assert.IsInstanceOfType(e, typeof(ResolutionFailedException));
                }

            }
        }
        
        // Test types 
        public interface ITestClass
        { }

        public class TestClass : ITestClass
        {
            public TestClass()
            { }

            [InjectionConstructor]
            public TestClass(TestClass x) //1
            { }
        }
    }
}
