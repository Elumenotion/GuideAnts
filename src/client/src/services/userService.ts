import { api } from './api';

interface UserInfo {
  id?: string;
  name?: string;
  email?: string;
}

class UserService {
  private static instance: UserService;
  private userCache = new Map<string, UserInfo>();
  private currentUser: UserInfo | null = null;

  private constructor() {}

  public static getInstance(): UserService {
    if (!UserService.instance) {
      UserService.instance = new UserService();
    }
    return UserService.instance;
  }

  /**
   * Get current logged-in user information
   */
  public async getCurrentUser(): Promise<UserInfo | null> {
    if (this.currentUser) {
      return this.currentUser;
    }

    this.currentUser = { name: 'OSS Lite User', email: 'oss-lite-user' };
    return this.currentUser;
  }

  /**
   * Check if a user ID belongs to the current user
   */
  public async isCurrentUser(_userId: string): Promise<boolean> {
    // For now, we can't easily determine if a database user ID belongs to current user
    // without additional server endpoint. This will be false for most cases until 
    // we implement proper current user ID lookup
    return false;
  }

  /**
   * Get user information by ID (with caching)
   */
  public async getUserById(userId: string): Promise<UserInfo | null> {
    // Check cache first
    if (this.userCache.has(userId)) {
      return this.userCache.get(userId)!;
    }

    try {
      // Fetch user from server by database ID
      const user = await api.users.getUserById(userId);
      const userInfo: UserInfo = {
        id: user.id,
        name: user.name,
        email: user.email
      };
      this.userCache.set(userId, userInfo);
      return userInfo;
    } catch (error) {
      console.warn(`Failed to fetch user ${userId}:`, error);
      
      // Create minimal fallback that will show generic "U"
      const fallbackUser: UserInfo = { 
        id: userId
        // No name or email - this will make UserAvatar fall back to "U"
      };
      this.userCache.set(userId, fallbackUser);
      return fallbackUser;
    }
  }

  /**
   * Get user info for a message - either current user or fetch by ID
   */
  public async getUserForMessage(userId?: string): Promise<UserInfo | null> {
    if (!userId) {
      // No user ID, try to get current user
      return await this.getCurrentUser();
    }

    // Check if this is the current user
    if (await this.isCurrentUser(userId)) {
      return await this.getCurrentUser();
    }

    // Try to fetch user by ID
    return await this.getUserById(userId);
  }

  /**
   * Clear cache (useful for testing or when user logs out)
   */
  public clearCache(): void {
    this.userCache.clear();
    this.currentUser = null;
  }

  /**
   * Extract initials from user info
   */
  public getUserInitials(userInfo: UserInfo | null): string {
    if (!userInfo) return 'U';

    if (userInfo.name) {
      const nameParts = userInfo.name.trim().split(/\s+/);
      if (nameParts.length >= 2) {
        return `${nameParts[0][0]}${nameParts[1][0]}`.toUpperCase();
      }
      return nameParts[0][0].toUpperCase();
    }

    if (userInfo.email) {
      const emailPart = userInfo.email.split('@')[0];
      const parts = emailPart.split(/[._-]/);
      if (parts.length >= 2) {
        return `${parts[0][0]}${parts[1][0]}`.toUpperCase();
      }
      return parts[0][0].toUpperCase();
    }

    return 'U';
  }
}

export const userService = UserService.getInstance();
export type { UserInfo }; 