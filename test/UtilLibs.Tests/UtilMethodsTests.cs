using UtilLibs;
using Xunit;

namespace UtilLibs.Tests
{
	public class UtilMethodsTests
	{
		[Theory]
		[InlineData(273.15f, 0f)]
		[InlineData(373.15f, 100f)]
		[InlineData(0f, -273.15f)]
		public void GetCFromKelvin_Converts(float kelvin, float expectedCelsius)
		{
			Assert.Equal(expectedCelsius, UtilMethods.GetCFromKelvin(kelvin), 3);
		}

		[Theory]
		[InlineData(0f, 273.15f)]
		[InlineData(100f, 373.15f)]
		public void GetKelvinFromC_Converts(float celsius, float expectedKelvin)
		{
			Assert.Equal(expectedKelvin, UtilMethods.GetKelvinFromC(celsius), 3);
		}

		[Theory]
		[InlineData(0f)]
		[InlineData(20f)]
		[InlineData(-40f)]
		public void KelvinCelsius_RoundTrips(float celsius)
		{
			Assert.Equal(celsius, UtilMethods.GetCFromKelvin(UtilMethods.GetKelvinFromC(celsius)), 3);
		}
	}
}
