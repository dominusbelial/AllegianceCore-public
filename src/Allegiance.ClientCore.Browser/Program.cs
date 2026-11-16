using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Allegiance.ClientCore.Browser
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            // Explicit platform registration avoids reflection-based factory discovery after trimming.
            Microsoft.Xna.Platform.GameFactory.RegisterGameFactory(new Microsoft.Xna.Platform.ConcreteGameFactory());
            Microsoft.Xna.Platform.Input.InputFactory.RegisterInputFactory(new Microsoft.Xna.Platform.Input.ConcreteInputFactory());
            Microsoft.Xna.Platform.Graphics.GraphicsFactory.RegisterGraphicsFactory(new Microsoft.Xna.Platform.Graphics.ConcreteGraphicsFactory());
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");
            builder.Services.AddScoped(sp => new HttpClient()
            {
                BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
            });
            await builder.Build().RunAsync();
        }
    }
}
