const authService = {
  getAccessToken(): string {
    return 'oss-lite-token';
  },
  getActiveAccount(): null {
    return null;
  },
  initialize(): void {},
  signIn(): void {},
  signOut(): void {},
  isReady(): boolean {
    return true;
  },
};

export { authService };
