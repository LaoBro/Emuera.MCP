using Microsoft.Maui.Storage;
using MinorShift.Emuera;

namespace Emuera.AndroidNativeAot;

public static class MauiProgram
{
		public static MauiApp CreateMauiApp()
		{
				var builder = MauiApp.CreateBuilder();
				builder.UseMauiApp<App>();

				// Root the shared engine assembly and exercise its platform-neutral initialization path.
				AppDataPaths.Configure(FileSystem.AppDataDirectory);

				return builder.Build();
		}
}
