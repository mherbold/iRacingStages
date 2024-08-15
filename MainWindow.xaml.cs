
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

using HerboldRacing;

namespace iRacingStages
{
	public partial class MainWindow : Window
	{
		public const string AppName = "iRacingStages";

		public static readonly string documentsFolder = Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments ) + $"\\{AppName}\\";

		private readonly Regex numbersOnlyRegex = MyRegex();

		[GeneratedRegex( "^[1-9][0-9]?[0-9]?$" )]
		private static partial Regex MyRegex();

		readonly IRSDKSharper irsdk = new();

		int stage1LapCount = 30;
		int stage2LapCount = 30;
		int numCarsToWaitFor = 10;

		bool initialized = false;
		IRacingSdkDatum? carIdxLapCompletedDatum = null;
		IRacingSdkDatum? carIdxLapDistPctDatum = null;
		IRacingSdkDatum? carIdxPositionDatum = null;
		int currentStage = 0;
		int numWinnersSoFar = 0;
		int[] stageWinners = new int[ IRacingSdkConst.MaxNumCars ];

		[DllImport( "user32.dll", SetLastError = true )]
		static extern IntPtr FindWindow( string? lpClassName, string lpWindowName );

		[DllImport( "user32.dll" )]
		[return: MarshalAs( UnmanagedType.Bool )]
		static extern bool SetForegroundWindow( IntPtr hWnd );

		public MainWindow()
		{
			InitializeComponent();

			irsdk.UpdateInterval = 10;

			irsdk.OnException += OnException;
			irsdk.OnStopped += OnStopped;
			irsdk.OnTelemetryData += OnTelemetryData;

			irsdk.Start();
		}

		private void Window_Closing( object? sender, CancelEventArgs e )
		{
			irsdk.Stop();
		}

		private void OnException( Exception exception )
		{
			irsdk.Stop();
		}

		private void OnStopped()
		{
			irsdk.Start();
		}

		private void OnTelemetryData()
		{
			// keep pointer to session info in case it gets updated during this process

			var sessionInfo = irsdk.Data.SessionInfo;

			if ( ( sessionInfo == null ) || ( sessionInfo.SessionInfo == null ) )
			{
				return;
			}

			// initialize stuff

			if ( !initialized )
			{
				initialized = true;

				carIdxLapCompletedDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxLapCompleted" ];
				carIdxLapDistPctDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxLapDistPct" ];
				carIdxPositionDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxPosition" ];
			}

			// make sure we are in a race session

			var sessionNumber = irsdk.Data.GetInt( "SessionNum" );

			if ( sessionInfo.SessionInfo.Sessions[ sessionNumber ].SessionType != "Race" )
			{
				return;
			}

			// make sure we are in stage 1 or 2

			if ( currentStage >= 2 )
			{
				return;
			}

			//

			if ( carIdxLapCompletedDatum != null && carIdxLapDistPctDatum != null && carIdxPositionDatum != null )
			{
				// figure out which lap we are looking for

				var targetLap = ( currentStage == 0 ) ? stage1LapCount : stage1LapCount + stage2LapCount;

				// get a list of the laps completed for all cars

				int[] carIdxLapCompletedList = new int[ carIdxLapCompletedDatum.Count ];

				irsdk.Data.GetIntArray( carIdxLapCompletedDatum, carIdxLapCompletedList, 0, carIdxLapCompletedDatum.Count );

				// get a list of the lap dist pct for all cars

				float[] carIdxLapDistPctList = new float[ carIdxLapDistPctDatum.Count ];

				irsdk.Data.GetFloatArray( carIdxLapDistPctDatum, carIdxLapDistPctList, 0, carIdxLapDistPctDatum.Count );

				// remove cars we've already recognized as winners

				for ( var i = 0; i < numWinnersSoFar; i++ )
				{
					carIdxLapCompletedList[ stageWinners[ i ] ] = -1;
				}

				// go through cars and add more winners to the list if there are any

				do
				{
					var highestLapDistPct = 0.0f;
					var nextWinner = -1;

					for ( var i = 0; i < carIdxLapCompletedDatum.Count; i++ )
					{
						if ( carIdxLapCompletedList[ i ] >= targetLap )
						{
							if ( carIdxLapDistPctList[ i ] > highestLapDistPct )
							{
								highestLapDistPct = carIdxLapDistPctList[ i ];
								nextWinner = i;
							}
						}
					}

					if ( nextWinner == -1 )
					{
						break;
					}
					else
					{
						stageWinners[ numWinnersSoFar ] = nextWinner;

						numWinnersSoFar++;

						Debug.WriteLine( $"Position {numWinnersSoFar} stage winner = {nextWinner}" );

						carIdxLapCompletedList[ nextWinner ] = -1;

						if ( numWinnersSoFar >= numCarsToWaitFor )
						{
							break;
						}
					}
				}
				while ( true );

				// if we've reached our target number of winners then throw the caution flag

				if ( numWinnersSoFar >= numCarsToWaitFor )
				{
					// advance to the next stage

					currentStage++;

					Debug.WriteLine( $"Stage {currentStage} complete!" );

					// are we already under caution?

					var sessionFlags = irsdk.Data.GetBitField( "SessionFlags" );

					if ( ( sessionFlags & ( (uint) IRacingSdkEnum.Flags.Caution | (uint) IRacingSdkEnum.Flags.CautionWaving ) ) != 0 )
					{
						// yes - use positions from iracing instead of ours
						Debug.WriteLine( $"!!!ALREADY UNDER CAUTION!!!" );

						int[] carIdxPositionList = new int[ carIdxPositionDatum.Count ];

						irsdk.Data.GetIntArray( carIdxPositionDatum, carIdxPositionList, 0, carIdxPositionDatum.Count );

						for ( int i = 0; i < numCarsToWaitFor; i++ )
						{
							var position = i + 1;

							for ( int j = 0; i < carIdxPositionList.Length; j++ )
							{
								if ( carIdxPositionList[ j ] == position )
								{
									stageWinners[ i ] = j;
									break;
								}
							}
						}
					}

					// throw the caution flag

					Debug.WriteLine( $"Throwing caution flag." );

					SendChatMessage( $"!y Stage {currentStage} complete!\r" );

					// convert stage winners array into a text string for the chat

					var stageWinnersAsText = "";

					for ( var i = 0; i < numCarsToWaitFor; i++ )
					{
						var position = i + 1;

						foreach ( var driver in sessionInfo.DriverInfo.Drivers )
						{
							if ( driver.CarIdx == stageWinners[ i ] )
							{
								var carNumberRaw = driver.CarNumberRaw;

								stageWinnersAsText += $" {position}-{carNumberRaw}";

								break;
							}
						}
					}

					// announce who the winners are

					Debug.WriteLine( $"Winners are:{stageWinnersAsText}" );

					SendChatMessage( $"/all Stage {currentStage}:{stageWinnersAsText}\r" );

					// save stage winners to file

					if ( !Directory.Exists( documentsFolder ) )
					{
						Directory.CreateDirectory( documentsFolder );
					}

					var stageWinnersFileText = $"Stage {currentStage} winners:\r\n\r\n";

					for ( var i = 0; i <= numCarsToWaitFor; i++ )
					{
						var position = i + 1;

						foreach ( var driver in sessionInfo.DriverInfo.Drivers )
						{
							if ( driver.CarIdx == stageWinners[ i ] )
							{
								var carNumberRaw = driver.CarNumberRaw;
								var driverName = driver.UserName;

								stageWinnersFileText += $"P{position}: #{carNumberRaw} - {driverName}\r\n";

								break;
							}
						}
					}

					stageWinnersFileText += "\r\n\r\n";

					var sessionUniqueID = irsdk.Data.GetInt( "SessionUniqueID" );

					var stageWinnersPath = $"{documentsFolder}{sessionUniqueID}.txt";

					File.AppendAllText( stageWinnersPath, stageWinnersFileText );

					// reset stuff for the next stage

					numWinnersSoFar = 0;

					// exit the app if we are all done

					if ( currentStage == 2 )
					{
						Debug.WriteLine( $"Stage 2 is done - exiting app..." );

						Dispatcher.BeginInvoke( () =>
						{
							Close();
						} );
					}
				}
			}
		}

		private void SendChatMessage( string chatMessage )
		{
			var windowHandle = (IntPtr?) FindWindow( null, "iRacing.com Simulator" );

			if ( windowHandle != null )
			{
				SetForegroundWindow( (IntPtr) windowHandle );

				irsdk.ChatComand( IRacingSdkEnum.ChatCommandMode.BeginChat, 0 );

				SendKeys.SendWait( chatMessage );
			}
		}

		private void AllowThreeDigitsNumbersOnly( object sender, TextCompositionEventArgs e )
		{
			var textBox = (System.Windows.Controls.TextBox) sender;

			var textIsAllowed = numbersOnlyRegex.IsMatch( $"{textBox.Text}{e.Text}" );

			e.Handled = !textIsAllowed;
		}

		private void stage1LapCountTextBox_TextChanged( object sender, System.Windows.Controls.TextChangedEventArgs e )
		{
			var textBox = (System.Windows.Controls.TextBox) sender;

			if ( !int.TryParse( textBox.Text, out stage1LapCount ) )
			{
				stage1LapCount = 30;
			}
		}

		private void stage2LapCountTextBox_TextChanged( object sender, System.Windows.Controls.TextChangedEventArgs e )
		{
			var textBox = (System.Windows.Controls.TextBox) sender;

			if ( !int.TryParse( textBox.Text, out stage2LapCount ) )
			{
				stage2LapCount = 30;
			}
		}

		private void numCarsToWaitForTextBox_TextChanged( object sender, System.Windows.Controls.TextChangedEventArgs e )
		{
			var textBox = (System.Windows.Controls.TextBox) sender;

			if ( !int.TryParse( textBox.Text, out numCarsToWaitFor ) )
			{
				numCarsToWaitFor = 10;
			}
		}
	}
}
