using System.Collections.Generic;
using JetBrains.Application.UI.Controls;
using JetBrains.Application.UI.Controls.GotoByName;
using JetBrains.Application.UI.Controls.JetPopupMenu;
using JetBrains.Application.UI.Controls.JetPopupMenu.Detail;
using JetBrains.Diagnostics;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Plugins.Unity.ProjectModel;
using JetBrains.ReSharper.Plugins.Unity.Resources.Icons;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Rider.Model;

namespace JetBrains.ReSharper.Plugins.Unity.Rider.CodeInsights
{
    [SolutionComponent]
    public class UnityCodeInsightFieldUsageProvider : AbstractUnityCodeInsightProvider
    {
        public override string ProviderId => "Unity serialized field";
        public override string DisplayName => "Unity serialized field";
        public override CodeLensAnchorKind DefaultAnchor => CodeLensAnchorKind.Right;
        public override ICollection<CodeLensRelativeOrdering> RelativeOrderings => new [] {new CodeLensRelativeOrderingLast()};

        public UnityCodeInsightFieldUsageProvider(UnitySolutionTracker unitySolutionTracker, UnityHost host, BulbMenuComponent bulbMenu)
            : base(unitySolutionTracker, host, bulbMenu)
        {
        }

        public override void OnClick(CodeInsightsHighlighting highlighting, ISolution solution)
        {
            Shell.Instance.GetComponent<JetPopupMenus>().Show(solution.GetLifetime(), JetPopupMenu.ShowWhen.NoItemsBannerIfNoItems, (lifetime, menu) =>
            {
                menu.Caption.Value = WindowlessControlAutomation.Create("Unity Editor values");
                menu.KeyboardAcceleration.Value = KeyboardAccelerationFlags.QuickSearch;

                menu.ItemKeys.AddRange(new [] {"Directional Light"});

                menu.DescribeItem.Advise(lifetime, e =>
                {

                    var displayText = "Directional Light ";
                    e.Descriptor.Text = displayText;
                    OccurrencePresentationUtil.AppendRelatedFile( e.Descriptor, "SampleScene.unity");
            
                    e.Descriptor.Icon = UnityFileTypeThemedIcons.FileUnity.Id;
                    
                });

                menu.ItemClicked.Advise(lifetime, key =>
                {

                });
            });
        }
    }
}