using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using EasyGet.Controls;
using Xunit;

namespace EasyGet.Tests;

public class CachedPageHostTests
{
    [Fact]
    public void PageViewsAreCreatedOncePerViewModelReference()
    {
        RunInSta(() =>
        {
            var host = new CachedPageHost();
            var template = (DataTemplate)XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Border />
                </DataTemplate>
                """);
            host.Resources.Add(new DataTemplateKey(typeof(EquivalentPage)), template);

            var firstPage = new EquivalentPage("first");
            var equivalentPage = new EquivalentPage("second");

            host.Page = firstPage;
            var firstView = Assert.IsType<Border>(host.Content);
            Assert.Same(firstPage, firstView.DataContext);

            host.Page = equivalentPage;
            var secondView = Assert.IsType<Border>(host.Content);
            Assert.NotSame(firstView, secondView);
            Assert.Same(equivalentPage, secondView.DataContext);

            host.Page = firstPage;
            Assert.Same(firstView, host.Content);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class EquivalentPage(string name)
    {
        public string Name { get; } = name;

        public override bool Equals(object? obj) => obj is EquivalentPage;

        public override int GetHashCode() => 0;
    }
}
