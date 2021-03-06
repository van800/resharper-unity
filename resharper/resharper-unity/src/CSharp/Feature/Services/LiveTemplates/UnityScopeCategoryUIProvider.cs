﻿using System.Collections.Generic;
using JetBrains.ReSharper.Feature.Services.LiveTemplates.Scope;
using JetBrains.ReSharper.Plugins.Unity.CSharp.Feature.Services.LiveTemplates.Scope;
using JetBrains.ReSharper.Plugins.Unity.Resources;

namespace JetBrains.ReSharper.Plugins.Unity.CSharp.Feature.Services.LiveTemplates
{
    [ScopeCategoryUIProvider(Priority = Priority)]
    public class UnityScopeCategoryUIProvider : ScopeCategoryUIProvider
    {
        // Needs to be less than other priorities in R#'s built in ScopeCategoryUIProvider
        // to push it to the end of the list
        private const int Priority = -200;

        public UnityScopeCategoryUIProvider()
            : base(LogoThemedIcons.UnityLogo.Id)
        {
            MainPoint = new InUnityShaderLabFile();
        }

        public override IEnumerable<ITemplateScopePoint> BuildAllPoints()
        {
            yield return new IsAvailableForClassAttribute();
            yield return new InUnityCSharpProject();
            yield return new MustBeInUnityType();
            yield return new InUnityShaderLabFile();
        }

        public override string CategoryCaption => "Unity";

        public override string Present(ITemplateScopePoint point)
        {
            if (point is IsAvailableForClassAttribute)
                return "In C# file where class attribute is available";
            if (point is InUnityCSharpProject)
                return "In Unity project";
            if (point is MustBeInUnityType)
                return "In Unity type where type members are allowed";
            if (point is InUnityShaderLabFile)
                return "In Unity ShaderLab file";
            return base.Present(point);
            
        }
    }
}