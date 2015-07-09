﻿using Hudl.Config;
using Hudl.Mjolnir.Tests.Util;

namespace Hudl.Mjolnir.Tests.Helper
{
    public class TestFixture
    {
        public TestFixture()
        {
            UseTempConfig();
        }

        protected void SetUpConfig()
        {
            
        }


        private static void UseTempConfig()
        {
            var provider = new TestConfigProvider();
            provider.Set("mjolnir.useCircuitBreakers", true);

            ConfigProvider.UseProvider(new TestConfigProvider());
        }
    }
}
