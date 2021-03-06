﻿//
// ConsoleApp.cs
//
// Author:
//   Aaron Bockover <aaron.bockover@gmail.com>
//
// Copyright 2015 Aaron Bockover. All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

using Mono.Options;

using Conservatorio.Rdio;

namespace Conservatorio
{
	public class ConsoleApp
	{
		Api api;
		string outputDir;
		readonly List<RdioUserKeyStore> userKeyStores = new List<RdioUserKeyStore> ();
		RdioObjectStore sharedObjectStore;

		public int Main (IEnumerable<string> args)
		{
			var showHelp = false;
			var following = false;
			outputDir = Environment.CurrentDirectory;

			Console.WriteLine ("Conservatorio v{0} ({1}/{2}, {3})",
				BuildInfo.Version, BuildInfo.Branch, BuildInfo.Hash, BuildInfo.Date);
			Console.WriteLine ("  by Aaron Bockover (@abock)");
			Console.WriteLine ("  http://conservator.io");
			Console.WriteLine ();

			var optionSet = new OptionSet {
				"Usage: conservatorio [OPTIONS]+ USER [USER...]",
				"",
				"Fetch users' favorites and synced Rdio track metadata as well as all " +
				"favorited, owned, collaborated, and subscriped playlists metadata, " +
				"backing up all raw JSON data as exported by Rdio.",
				"",
				"More information: http://conservator.io",
				"",
				"Options:",
				{ "o|output-dir=",
					"output all JSON data to files in {DIR} directory," +
					"defaulting to the current working directory",
					v => outputDir = v },
				{ "s|single-store",
					"when fetching data for multiple users, use a single shared object " +
					"store and persist all users in the same output file",
					v => sharedObjectStore = new RdioObjectStore () },
				{ "f|following",
					"also back up the data of all users a specified user is " +
					"following (non recursive)",
					v => following = true },
				{ "h|help", "show this help", v => showHelp = true }
			};

			var users = optionSet.Parse (args);

			if (showHelp || users.Count == 0) {
				optionSet.WriteOptionDescriptions (Console.Out);
				return 1;
			}

			if (!Directory.Exists (outputDir)) {
				Console.Error.WriteLine ("error: output directory does not exist: {0}", outputDir);
				return 1;
			}

			api = new Api ();

			foreach (var user in users)
				SyncUser (user, following).Wait ();

			if (sharedObjectStore != null && userKeyStores.Count > 0) {
				var path = Path.Combine (outputDir, "Conservatorio_RdioExport.json");
				Console.WriteLine ("Exporting data for {0} users ({1} total objects) to {2}...",
					userKeyStores.Count, sharedObjectStore.Count, path);
				new Exporter {
					ObjectStore = sharedObjectStore,
					UserKeyStores = userKeyStores
				}.Export (path);
				Console.WriteLine ("Done!");
			}

			return 0;
		}

		void DisplayStatusBar (int current, int total)
		{
			const int barWidth = 30;
			var progress = current / (double)total;
			int barProgress = (int)Math.Round (barWidth * progress);

			Console.Write ("\r    [");
			for (int i = 0; i < barWidth; i++) {
				if (i <= barProgress)
					Console.Write ('#');
				else
					Console.Write (' ');
			}
			Console.Write ("] ");
			Console.Write ("{0:0.0}% ({1} / {2}) ", progress * 100, current, total);
		}

		public async Task SyncUser (string userId, bool syncFollowing)
		{
			var startTime = DateTime.UtcNow;
			var syncController = new UserSyncController (api, userId,
				sharedObjectStore ?? new RdioObjectStore ());

			do {
				try {
					await syncController.SyncStepAsync (() => DisplayStatusBar (
						syncController.SyncedObjects, syncController.TotalObjects));
				} catch (Exception e) {
					var webException = e as WebException;
					if (webException != null)
						Console.WriteLine ("  ! Connection Error");
					else if (e is UserNotFoundException)
						Console.WriteLine ("  ! Could not find Rdio user {0}",
							syncController.UserIdentifier);
					else if (e is OperationCanceledException)
						Console.WriteLine ("  ! Canceled");
					else
						Console.WriteLine (e);

					syncController = null;
					return;
				}

				switch (syncController.SyncState) {
				case SyncState.Start:
					break;
				case SyncState.FindingUser:
					Console.WriteLine ("Starting work for '{0}'...",
						syncController.UserIdentifier);
					break;
				case SyncState.FoundUser:
					Console.WriteLine ("  * Resolved user {0} ({1})...",
						syncController.UserKeyStore.User.DisplayName,
						syncController.UserKeyStore.User.Key);
					break;
				case SyncState.SyncingUserKeys:
					Console.WriteLine ("  * Fetching keys...");
					break;
				case SyncState.SyncedUserKeys:
					Console.WriteLine ("    {0} toplevel keys of interest",
						syncController.UserKeyStore.TotalKeys);
					break;
				case SyncState.SyncingObjects:
					Console.WriteLine ("  * Fetching objects...");
					break;
				case SyncState.SyncedObjects:
					Console.WriteLine ();
					Console.WriteLine ("    {0} objects fetched",
						syncController.TotalObjects);
					break;
				case SyncState.Finished:
					Console.WriteLine ("  * Fetched in {0}",
						(DateTime.UtcNow - startTime).ToFriendlyString ());

					if (sharedObjectStore == null) {
						var path = Path.Combine (outputDir, syncController.FileName);
						Console.WriteLine ("  * Exporting to {0}...", path);
						syncController.CreateExporter ().Export (path);
					} else
						userKeyStores.Add (syncController.UserKeyStore);

					Console.WriteLine ("  * Done!");
					break;
				}
			} while (syncController.SyncState != SyncState.Finished);

			if (syncFollowing) {
				var user = syncController.UserKeyStore.User;
				foreach (var followingKey in api.GetUserFollowingAsync (user).Result)
					SyncUser (followingKey, false).Wait ();
			}
		}
	}
}