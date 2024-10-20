
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using IRSDKSharper;

#pragma warning disable CS8602
#pragma warning disable CS8604

namespace iRacingStages
{
	public partial class MainWindow : Window
	{
		public const string AppName = "iRacingStages";

		public static readonly string documentsFolder = Environment.GetFolderPath( Environment.SpecialFolder.MyDocuments ) + $"\\{AppName}\\";

		private readonly Regex numbersOnlyRegex = NumbersOnlyRegex();

		[GeneratedRegex( @"^[1-9][0-9]?[0-9]?$" )]
		private static partial Regex NumbersOnlyRegex();

		private readonly Regex trackLengthRegex = TrackLengthRegex();

		[GeneratedRegex( @"([-+]?[0-9]*\.?[0-9]+)" )]
		private static partial Regex TrackLengthRegex();

		readonly IRacingSdk irsdk = new();

		int stage1LapCount = 30;
		int stage2LapCount = 30;
		int stage3LapCount = 30;
		int numCarsToWaitFor = 10;
		bool throwTheCautionFlag = true;
		bool automaticallyCloseThisApp = true;

		bool initialized = false;

		IntPtr? windowHandle = null;

		IRacingSdkDatum? sessionNumDatum = null;
		IRacingSdkDatum? sessionFlagsDatum = null;
		IRacingSdkDatum? carIdxLapDatum = null;
		IRacingSdkDatum? carIdxLapCompletedDatum = null;
		IRacingSdkDatum? carIdxLapDistPctDatum = null;
		IRacingSdkDatum? carIdxPositionDatum = null;
		IRacingSdkDatum? carIdxOnPitRoadDatum = null;

		string sessionType = string.Empty;
		int currentStage = 0;
		int completedLaps = 0;
		int numWinnersSoFar = 0;
		readonly int[] stageWinnerCarIdxList = new int[ IRacingSdkConst.MaxNumCars ];
		bool lastStageLapWarningShown = false;

		readonly List<string> chatMessageQueue = [];
		bool chatWindowOpened = false;
		bool mainWindowClosed = false;

		[DllImport( "user32.dll", SetLastError = true )]
		static extern IntPtr FindWindow( string? lpClassName, string lpWindowName );

		[return: MarshalAs( UnmanagedType.Bool )]
		[DllImport( "user32.dll", SetLastError = true, CharSet = CharSet.Auto )]
		static extern bool PostMessage( IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam );

		public MainWindow()
		{
			InitializeComponent();

			irsdk.UpdateInterval = 10;

			irsdk.OnException += OnException;
			irsdk.OnStopped += OnStopped;
			irsdk.OnTelemetryData += OnTelemetryData;
			irsdk.OnDebugLog += OnDebugLog;

			irsdk.Start();
		}

		private void Window_Closing( object? sender, CancelEventArgs e )
		{
			mainWindowClosed = true;

			irsdk.Stop();
		}

		private void OnException( Exception exception )
		{
			irsdk.Stop();
		}

		private void OnStopped()
		{
			if ( !mainWindowClosed )
			{
				irsdk.Start();
			}
		}

		private void OnTelemetryData()
		{
			// keep pointer to session info in case it gets updated during this process

			var sessionInfo = irsdk.Data.SessionInfo;

			// maybe session info is not ready yet - check for that

			if ( sessionInfo?.SessionInfo == null )
			{
				UpdateStatusBar();

				return;
			}

			// one time initialization

			if ( !initialized )
			{
				initialized = true;

				windowHandle = FindWindow( null, "iRacing.com Simulator" );

				sessionNumDatum = irsdk.Data.TelemetryDataProperties[ "SessionNum" ];
				sessionFlagsDatum = irsdk.Data.TelemetryDataProperties[ "SessionFlags" ];
				carIdxLapDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxLap" ];
				carIdxLapCompletedDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxLapCompleted" ];
				carIdxLapDistPctDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxLapDistPct" ];
				carIdxPositionDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxPosition" ];
				carIdxOnPitRoadDatum = irsdk.Data.TelemetryDataProperties[ "CarIdxOnPitRoad" ];
			}

			// don't do anything if we are not in the race session

			var sessionNumber = irsdk.Data.GetInt( sessionNumDatum );

			sessionType = sessionInfo.SessionInfo.Sessions[ sessionNumber ].SessionType;

			if ( sessionType != "Race" )
			{
				UpdateStatusBar();

				return;
			}

			// process chat message queue

			ProcessChatMessageQueue();

			// get session flags

			var sessionFlags = irsdk.Data.GetBitField( sessionFlagsDatum );

			// get a list of the current laps for all cars

			int[] carIdxLapList = new int[ carIdxLapDatum.Count ];

			irsdk.Data.GetIntArray( carIdxLapDatum, carIdxLapList, 0, carIdxLapDatum.Count );

			// get a list of the laps completed for all cars

			int[] carIdxLapCompletedList = new int[ carIdxLapCompletedDatum.Count ];

			irsdk.Data.GetIntArray( carIdxLapCompletedDatum, carIdxLapCompletedList, 0, carIdxLapCompletedDatum.Count );

			// get a list of the lap dist pct for all cars

			float[] carIdxLapDistPctList = new float[ carIdxLapDistPctDatum.Count ];

			irsdk.Data.GetFloatArray( carIdxLapDistPctDatum, carIdxLapDistPctList, 0, carIdxLapDistPctDatum.Count );

			// get a list of the pit road status for all cars

			bool[] carIdxOnPitRoadList = new bool[ carIdxOnPitRoadDatum.Count ];

			irsdk.Data.GetBoolArray( carIdxOnPitRoadDatum, carIdxOnPitRoadList, 0, carIdxOnPitRoadDatum.Count );

			// find the pace car

			int paceCarIdx = -1;

			foreach ( var driver in sessionInfo.DriverInfo.Drivers )
			{
				if ( ( driver.CarIdx >= 0 ) && ( driver.CarIdx < carIdxLapCompletedList.Length ) )
				{
					if ( driver.CarIsPaceCar == 1 )
					{
						paceCarIdx = driver.CarIdx;
						break;
					}
				}
			}

			// remove pace car from the list

			if ( paceCarIdx != -1 )
			{
				carIdxLapCompletedList[ paceCarIdx ] = -1;
			}

			// have any cars started lap 1?

			var anyCarStartedLapOne = false;

			for ( var carIdx = 0; carIdx < carIdxLapDatum.Count; carIdx++ )
			{
				if ( carIdxLapList[ carIdx ] >= 1 )
				{
					anyCarStartedLapOne = true;
					break;
				}
			}

			// if nobody is on lap 1 then reset everything (race has not started yet)

			if ( !anyCarStartedLapOne )
			{
				currentStage = 0;
				completedLaps = 0;
				numWinnersSoFar = 0;
				lastStageLapWarningShown = false;
			}

			// remove cars that are on pit road

			for ( var carIdx = 0; carIdx < carIdxOnPitRoadList.Length; carIdx++ )
			{
				if ( carIdxOnPitRoadList[ carIdx ] )
				{
					carIdxLapCompletedList[ carIdx ] = -1;
				}
			}

			// remove spectators from the list

			foreach ( var driver in sessionInfo.DriverInfo.Drivers )
			{
				if ( ( driver.CarIdx >= 0 ) && ( driver.CarIdx < carIdxLapCompletedList.Length ) )
				{
					if ( driver.IsSpectator == 1 )
					{
						carIdxLapCompletedList[ driver.CarIdx ] = -1;
					}
				}
			}

			// if we are in the final stage then stop here

			if ( currentStage == 3 )
			{
				if ( automaticallyCloseThisApp && ( chatMessageQueue.Count == 0 ) && !chatWindowOpened )
				{
					Dispatcher.BeginInvoke( () =>
					{
						Close();
					} );
				}

				return;
			}

			// figure out which lap we are targeting for this stage

			int previousStageLaps = 0;
			int thisStageLaps = 0;

			switch ( currentStage )
			{
				case 0:
					previousStageLaps = 0;
					thisStageLaps = stage1LapCount;
					break;

				case 1:
					previousStageLaps = stage1LapCount;
					thisStageLaps = stage2LapCount;
					break;

				case 2:
					previousStageLaps = stage1LapCount + stage2LapCount;
					thisStageLaps = stage3LapCount;
					break;
			}

			var targetLap = previousStageLaps + thisStageLaps;

			// if laps is set to 0 for this stage then skip to the next stage

			if ( thisStageLaps == 0 )
			{
				currentStage++;

				UpdateStatusBar();

				return;
			}

			// reset completed laps

			completedLaps = 0;

			// remove cars we've already recognized as winners

			for ( var stageWinnerIdx = 0; stageWinnerIdx < numWinnersSoFar; stageWinnerIdx++ )
			{
				carIdxLapCompletedList[ stageWinnerCarIdxList[ stageWinnerIdx ] ] = -1;
			}

			// go through cars and add more winners to the list if there are any

			do
			{
				var highestLapDistPct = 0.0f;
				var nextWinnerCarIdx = -1;

				for ( var carIdx = 0; carIdx < carIdxLapCompletedDatum.Count; carIdx++ )
				{
					if ( carIdxLapCompletedList[ carIdx ] > completedLaps )
					{
						completedLaps = carIdxLapCompletedList[ carIdx ];
					}

					if ( carIdxLapCompletedList[ carIdx ] >= targetLap )
					{
						if ( carIdxLapDistPctList[ carIdx ] > highestLapDistPct )
						{
							highestLapDistPct = carIdxLapDistPctList[ carIdx ];
							nextWinnerCarIdx = carIdx;
						}
					}
				}

				if ( nextWinnerCarIdx == -1 )
				{
					break;
				}
				else
				{
					stageWinnerCarIdxList[ numWinnersSoFar ] = nextWinnerCarIdx;

					numWinnersSoFar++;

					carIdxLapCompletedList[ nextWinnerCarIdx ] = -1;

					if ( numWinnersSoFar >= numCarsToWaitFor )
					{
						break;
					}
				}
			}
			while ( true );

			// if we are on the last stage lap then warn the drivers

			if ( completedLaps == ( targetLap - 1 ) )
			{
				if ( !lastStageLapWarningShown )
				{
					lastStageLapWarningShown = true;

					chatMessageQueue.Add( $"/all Final lap for stage {currentStage + 1}!\r" );
				}
			}

			// if we've reached our target number of winners then throw the caution flag

			if ( numWinnersSoFar >= numCarsToWaitFor )
			{
				// advance to the next stage

				currentStage++;

				// are we already under caution?

				if ( ( sessionFlags & ( (uint) IRacingSdkEnum.Flags.Caution | (uint) IRacingSdkEnum.Flags.CautionWaving ) ) != 0 )
				{
					// yes - use positions from iracing instead of ours

					int[] carIdxPositionList = new int[ carIdxPositionDatum.Count ];

					irsdk.Data.GetIntArray( carIdxPositionDatum, carIdxPositionList, 0, carIdxPositionDatum.Count );

					for ( int stageWinnerIdx = 0; stageWinnerIdx < numCarsToWaitFor; stageWinnerIdx++ )
					{
						var position = stageWinnerIdx + 1;

						for ( int carIdx = 0; carIdx < carIdxPositionList.Length; carIdx++ )
						{
							if ( carIdxPositionList[ carIdx ] == position )
							{
								stageWinnerCarIdxList[ stageWinnerIdx ] = carIdx;
								break;
							}
						}
					}

					// tell drivers the stage is complete

					chatMessageQueue.Add( $"/all Stage {currentStage} complete!\r" );
				}
				else if ( throwTheCautionFlag )
				{
					// throw the caution flag

					chatMessageQueue.Add( $"!y Stage {currentStage} complete!\r" );
				}
				else
				{
					// tell drivers the stage is complete

					chatMessageQueue.Add( $"/all Stage {currentStage} complete!\r" );
				}

				// convert stage winners array into a text string for admin chat

				var stageWinnersAdminChatText = $"Stage {currentStage}:";

				for ( var stageWinnerIdx = 0; stageWinnerIdx < numCarsToWaitFor; stageWinnerIdx++ )
				{
					var position = stageWinnerIdx + 1;

					foreach ( var driver in sessionInfo.DriverInfo.Drivers )
					{
						if ( driver.CarIdx == stageWinnerCarIdxList[ stageWinnerIdx ] )
						{
							var carNumberRaw = driver.CarNumberRaw;

							stageWinnersAdminChatText += $" {position}-{carNumberRaw}";

							break;
						}
					}
				}

				// announce who the winners are

				chatMessageQueue.Add( $"/all {stageWinnersAdminChatText}\r" );

				// save stage winners to file

				if ( !Directory.Exists( documentsFolder ) )
				{
					Directory.CreateDirectory( documentsFolder );
				}

				var stageWinnersFileText = $"Stage {currentStage} winners:\r\n\r\n";

				for ( var stageWinnerIdx = 0; stageWinnerIdx < numCarsToWaitFor; stageWinnerIdx++ )
				{
					var position = stageWinnerIdx + 1;

					foreach ( var driver in sessionInfo.DriverInfo.Drivers )
					{
						if ( driver.CarIdx == stageWinnerCarIdxList[ stageWinnerIdx ] )
						{
							var carNumberRaw = driver.CarNumberRaw;
							var driverName = driver.UserName;

							stageWinnersFileText += $"P{position}: #{carNumberRaw} - {driverName}\r\n";

							break;
						}
					}
				}

				stageWinnersFileText += "\r\n";

				var stageWinnersPath = $"{documentsFolder}{sessionInfo.WeekendInfo.SubSessionID}.txt";

				File.AppendAllText( stageWinnersPath, stageWinnersFileText );

				// reset stuff for the next stage

				numWinnersSoFar = 0;
				lastStageLapWarningShown = false;
			}

			UpdateStatusBar();
		}

		private void OnDebugLog( string message )
		{
			Debug.WriteLine( message );
		}

		private void UpdateStatusBar()
		{
			Dispatcher.BeginInvoke( () =>
			{
				currentStageLabel.Content = $"Current stage: {currentStage + 1}";
				completedLapsLabel.Content = $"Completed laps: {completedLaps}";
				carsFinishedLabel.Content = $"Cars finished: {numWinnersSoFar}";

				if ( irsdk.IsConnected )
				{
					if ( sessionType == "Race" )
					{
						connectionLabel.Content = $"{sessionType} 😊";
						connectionLabel.Foreground = new SolidColorBrush( System.Windows.Media.Color.FromRgb( 0, 127, 0 ) );
					}
					else
					{
						connectionLabel.Content = $"{sessionType} 😑";
						connectionLabel.Foreground = new SolidColorBrush( System.Windows.Media.Color.FromRgb( 63, 63, 0 ) );
					}
				}
				else
				{
					connectionLabel.Content = "NOT CONNECTED 😞";
					connectionLabel.Foreground = new SolidColorBrush( System.Windows.Media.Color.FromRgb( 127, 0, 0 ) );
				}
			} );
		}

		private void ProcessChatMessageQueue()
		{
			if ( chatMessageQueue.Count > 0 )
			{
				if ( chatWindowOpened )
				{
					if ( windowHandle != null )
					{
						string chatMessage = chatMessageQueue[ 0 ];

						Debug.WriteLine( $"Sending chat message: {chatMessage}" );

						foreach ( var ch in chatMessage )
						{
							PostMessage( (IntPtr) windowHandle, 0x0102, ch, 0 );
						}
					}

					chatMessageQueue.RemoveAt( 0 );

					if ( chatMessageQueue.Count > 0 )
					{
						chatWindowOpened = false;
					}
				}
				else
				{
					irsdk.ChatComand( IRacingSdkEnum.ChatCommandMode.BeginChat, 0 );

					chatWindowOpened = true;
				}
			}
			else
			{
				if ( chatWindowOpened )
				{
					irsdk.ChatComand( IRacingSdkEnum.ChatCommandMode.Cancel, 0 );

					chatWindowOpened = false;
				}
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

		private void stage3LapCountTextBox_TextChanged( object sender, System.Windows.Controls.TextChangedEventArgs e )
		{
			var textBox = (System.Windows.Controls.TextBox) sender;

			if ( !int.TryParse( textBox.Text, out stage3LapCount ) )
			{
				stage3LapCount = 30;
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

		private void throwTheCautionFlagCheckBox_Checked( object sender, RoutedEventArgs e )
		{
			throwTheCautionFlag = true;
		}

		private void throwTheCautionFlagCheckBox_Unchecked( object sender, RoutedEventArgs e )
		{
			throwTheCautionFlag = false;
		}

		private void automaticallyCloseThisAppCheckBox_Checked( object sender, RoutedEventArgs e )
		{
			automaticallyCloseThisApp = true;
		}

		private void automaticallyCloseThisAppCheckBox_Unchecked( object sender, RoutedEventArgs e )
		{
			automaticallyCloseThisApp = false;
		}
	}
}
