using System.Net;
using System.Text;
using Octokit;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.SettingsEngine;
using Windows.System;
using UniGetUI.Interface;

namespace UniGetUI.Services
{
    public class GitHubAuthService
    {
        private readonly string GitHubClientId = Secrets.GetGitHubClientId();
        private readonly string GitHubClientSecret = Secrets.GetGitHubClientSecret();
        private const string RedirectUri = "http://127.0.0.1:58642/";
        private readonly GitHubClient _client;

        public static event EventHandler<EventArgs>? AuthStatusChanged;
        public GitHubAuthService()
        {
            _client = new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName));
        }

        public GitHubClient? CreateGitHubClient()
        {
            var token = SecureGHTokenManager.GetToken();
            if (string.IsNullOrEmpty(token))
            {
                Logger.Error("GitHub access token is not available. Cannot perform Gist operation.");
                return null;
            }

            return new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName))
            {
                Credentials = new Credentials(token)
            };
        }

        private GHAuthApiRunner? loginBackend;
        public async Task<bool> SignInAsync()
        {
            try
            {
                Logger.Info("Initiating GitHub sign-in process using loopback redirect...");

                var request = new OauthLoginRequest(GitHubClientId)
                {
                    Scopes = { "read:user", "gist" },
                    RedirectUri = new Uri(RedirectUri)
                };

                var oauthLoginUrl = _client.Oauth.GetGitHubLoginUrl(request);

                codeFromAPI = null;
                if (loginBackend is not null)
                {
                    try
                    {
                        await loginBackend.Stop();
                        loginBackend.Dispose();
                        loginBackend = null;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex);
                    }
                }
                loginBackend = new GHAuthApiRunner();
                loginBackend.OnLogin += BackgroundApiOnOnLogin;
                await loginBackend.Start();
                await Launcher.LaunchUriAsync(oauthLoginUrl);

                while (codeFromAPI is null) await Task.Delay(100);

                loginBackend.OnLogin -= BackgroundApiOnOnLogin;
                await loginBackend.Stop();
                loginBackend.Dispose();
                loginBackend = null;

                return await _completeSignInAsync(codeFromAPI);
            }
            catch (Exception ex)
            {
                Logger.Error("Exception during GitHub sign-in process:");
                Logger.Error(ex);
                ClearAuthenticatedUserData();
                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }
        }

        private string? codeFromAPI;
        private void BackgroundApiOnOnLogin(object? sender, string c)
        {
            codeFromAPI = c;
        }

        private async Task<bool> _completeSignInAsync(string code)
        {
            try
            {
                var tokenRequest = new OauthTokenRequest(GitHubClientId, GitHubClientSecret, code)
                {
                    RedirectUri = new Uri(RedirectUri) // The same redirect_uri must be sent
                };
                var token = await _client.Oauth.CreateAccessToken(tokenRequest);

                if (string.IsNullOrEmpty(token.AccessToken))
                {
                    Logger.Error("Failed to obtain GitHub access token.");
                    AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                    return false;
                }

                Logger.Info("GitHub login successful. Storing access token.");
                SecureGHTokenManager.StoreToken(token.AccessToken);

                var userClient = new GitHubClient(new ProductHeaderValue("UniGetUI"))
                {
                    Credentials = new Credentials(token.AccessToken)
                };

                var user = await userClient.User.Current();
                if (user != null)
                {
                    Settings.SetValue(Settings.K.GitHubUserLogin, user.Login);
                    Logger.Info($"Logged in as GitHub user: {user.Login}");
                }
                else
                {
                    Logger.Warn("Could not retrieve GitHub user information after login.");
                }

                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Exception during GitHub token exchange:");
                Logger.Error(ex);
                ClearAuthenticatedUserData();
                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }
        }

        public void SignOut()
        {
            Logger.Info("Signing out from GitHub...");
            try
            {
                ClearAuthenticatedUserData();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to log out:");
                Logger.Error(ex);
            }

            AuthStatusChanged?.Invoke(this, EventArgs.Empty);
            Logger.Info("GitHub sign-out complete.");
        }

        private static void ClearAuthenticatedUserData()
        {
            Settings.SetValue(Settings.K.GitHubUserLogin, ""); // Clear stored username
            SecureGHTokenManager.DeleteToken();
        }

        public bool IsAuthenticated()
        {
            var token = SecureGHTokenManager.GetToken();
            return !string.IsNullOrEmpty(token);
        }
    }
}
