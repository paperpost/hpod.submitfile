using System;
using CommandLine;
using CommandLine.Text;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace SubmitFile {
	class Program {
		class Options {
			[Option('i', Required=true, HelpText="The DNS name for your H-POD instance")]
			public string instance { get; set; }

			[Option('f', Required=true, HelpText="Path to the file to submit")]
			public string file { get; set; }

			[Option('o', Required=true, HelpText ="Path to the .json file containing the print options")]
			public string optionsFile { get; set; }

			[Option('l', Required=false, HelpText ="File containing list of attachments to submit")]
			public string attachmentList { get; set; }

			[Option('a', Required =true, HelpText="Account login")]
			public string accountLogin { get; set; }

			[Option('u', Required=true, HelpText="User login")]
			public string email { get; set; }

			[Option('p', Required=true, HelpText ="Password")]
			public string password { get; set; }
		}

		static void Main(string[] args) {
			var result = Parser.Default.ParseArguments<Options>(args).MapResult(opts => SubmitDocument(opts).ConfigureAwait(false).GetAwaiter().GetResult(), _ => -1);
		}

		// Post with throttling retries
		private static async Task<HttpResponseMessage> CallWithRetryAsync(HttpClient client, string URL, HttpContent content, bool Post) {
			bool complete=false;
			int RetryInterval = 1000;
			HttpResponseMessage result = null;

			while(!complete) {
				if(Post) {
					result = await client.PostAsync(URL, content, System.Threading.CancellationToken.None).ConfigureAwait(false);
				} else {
					result = await client.GetAsync(URL, System.Threading.CancellationToken.None).ConfigureAwait(false);
				}

				var response = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
				bool throttled = false;

				if(result.StatusCode == System.Net.HttpStatusCode.OK) {
					var jResponse = JObject.Parse(response);

					throttled = ((string)jResponse["result"]["code"]=="429");
				}

				if(result.StatusCode == (System.Net.HttpStatusCode)429)
					throttled = true;

				if(throttled) {
					System.Threading.Thread.Sleep(RetryInterval);
					RetryInterval += (RetryInterval < 5000) ? 1000 : 5000;	// Retry in 1-second increments up until a 5-second interval, then use 5-second increments
				} else {
					complete = true;
				}
			}

			return result;
		}

		private static async Task<int> SubmitDocument(Options O) {
			using (HttpClient client = new HttpClient())
			{
				client.BaseAddress = new Uri(string.Format("https://{0}/api/v2/", O.instance));

				JObject oAuth = JObject.FromObject(new {
						accountLogin = O.accountLogin,
						email =  O.email,
						password = O.password
					});

				Console.Write("Authenticating...");

				// Acquire ticket
				var result = await CallWithRetryAsync(client, "authenticate",
								new System.Net.Http.StringContent(oAuth.ToString(),
																  System.Text.Encoding.UTF8,
																  "application/json"), true).ConfigureAwait(false);

				string authResponse = await result.Content.ReadAsStringAsync().ConfigureAwait(false);

				if(result.StatusCode == System.Net.HttpStatusCode.OK) {
					Console.WriteLine("OK");

					var response = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(authResponse, new { token = "" });

					client.DefaultRequestHeaders.Add("Authorization", "Bearer "+response.token);

					// print/init
					System.IO.StreamReader sr = new System.IO.StreamReader(O.optionsFile);
					string opts = sr.ReadToEnd();
					sr.Close();

					Console.Write("Initialising print job...");

					result = await CallWithRetryAsync(client, "print/init",
									new System.Net.Http.StringContent(opts, System.Text.Encoding.UTF8, "application/json"), true).ConfigureAwait(false);

					string initResponse = await result.Content.ReadAsStringAsync().ConfigureAwait(false);

					if(result.StatusCode == System.Net.HttpStatusCode.OK) {
						JObject jResponse = JObject.Parse(initResponse);

						Guid JobGUID = Guid.Parse((string)jResponse["jobGuid"]);

						Console.WriteLine("Job GUID {0}", JobGUID);

						client.DefaultRequestHeaders.Add("hpod-jobguid", JobGUID.ToString());

						Console.Write("Sending {0}...", O.file);

						if(await SendFileAsync(client, "print/docpart", O.file, 0).ConfigureAwait(false)) {
							Console.WriteLine("OK");

							bool AttachmentsOK = false;

							if(!string.IsNullOrEmpty(O.attachmentList)) {
								int attindex = 0;
								sr = new System.IO.StreamReader(O.attachmentList);

								while(!sr.EndOfStream) {
									string attachment = sr.ReadLine();

									if(!string.IsNullOrEmpty(attachment))
										if(System.IO.File.Exists(attachment)) {
											Console.Write("Sending attachment {0}...", attachment);
											if(!await SendFileAsync(client, "print/attpart", attachment, attindex++).ConfigureAwait(false)) {
												AttachmentsOK=false;
												break;
											} else
												Console.WriteLine("OK");
										}
								}

								sr.Close();
							} else {
								AttachmentsOK = true;
							}

							if(AttachmentsOK) {
								Console.Write("Committing submission for processing...");
								var CommitResult = await CallWithRetryAsync(client, "print/commit", null, false).ConfigureAwait(false);

								if(CommitResult.StatusCode == System.Net.HttpStatusCode.OK) {
									Console.WriteLine("OK");
									bool committed = false;

									Console.Write("Validating receipt of submission...");
									while(!committed) {
										result = await CallWithRetryAsync(client, "print/committed", null, false).ConfigureAwait(false);

										jResponse = JObject.Parse(await result.Content.ReadAsStringAsync());

										committed = (string)jResponse["result"]["code"]=="200";

										if(!committed) {
											System.Threading.Thread.Sleep(500);
											Console.Write("[retry] ");
										}
									}

									Console.WriteLine("OK");
								} else {
									Console.WriteLine("Failed to commit print job");
								}
							}
						} else {
							Console.WriteLine("Failed");
						}
					} else {
						Console.WriteLine("Failed");
						Console.WriteLine(initResponse);
					}
				} else {
					Console.WriteLine("Failed");
				}
			}

			return 0;
		}

		private static async Task<bool> SendFileAsync(HttpClient client, string endpoint, string path, int index) {
			try {
				// print/docpart
				int partSize = 2000;
				int totalRead = 0;
				int partindex = 0;
				byte[] part = new byte[partSize];

				System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Open);
				
				Console.WriteLine("");

				while(totalRead < fs.Length) {
					int read = fs.Read(part, 0, partSize);

					System.IO.MemoryStream ms = new System.IO.MemoryStream(part, 0, read);

					ms.Seek(0, System.IO.SeekOrigin.Begin);

					// add hpod-part header
					// add hpod-totalsize header

					HttpContent content = new StreamContent(ms);
					content.Headers.Add("hpod-part", partindex.ToString());
					content.Headers.Add("hpod-totalsize", fs.Length.ToString());
					content.Headers.Add("hpod-filetype", System.IO.Path.GetExtension(path).Substring(1));
					content.Headers.Add("hpod-fileindex", index.ToString());
					content.Headers.Add("hpod-filename", System.IO.Path.GetFileName(path));

					var result = await CallWithRetryAsync(client, endpoint, content, true).ConfigureAwait(false);

					string response = await result.Content.ReadAsStringAsync().ConfigureAwait(false);

					JObject jDocPartResponse = JObject.Parse(response);

					if((int)jDocPartResponse["result"]["code"]!=200) {
						throw new Exception(string.Format(" /// Failed to send part to {0}\r\n{1}", endpoint, response));
					} else {
						totalRead += read;
						Console.Write("\rSent [{0}/{1}] bytes ", totalRead, fs.Length);
					}

					partindex++;
				}

				fs.Close();

				return true;
			} catch (Exception e) {
				Console.WriteLine("Failed");
				Console.WriteLine(e.Message);
				return false;
			}
		}
	}
}
