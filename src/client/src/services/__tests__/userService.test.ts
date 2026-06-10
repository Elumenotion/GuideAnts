import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockAuthMe = vi.fn();
const mockGetUserById = vi.fn();

vi.mock('../api', () => ({
  api: {
    auth: {
      me: () => mockAuthMe(),
    },
    users: {
      getUserById: (userId: string) => mockGetUserById(userId),
    },
  },
}));

import { userService } from '../userService';

describe('userService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    userService.clearCache();
  });

  describe('getCurrentUser', () => {
    it('caches current user after first fetch', async () => {
      mockAuthMe.mockResolvedValue({
        userId: 'u1',
        name: 'Alice',
        email: 'alice@example.com',
        role: 'Contributor',
        mustChangePassword: false,
      });

      const first = await userService.getCurrentUser();
      const second = await userService.getCurrentUser();

      expect(first).toEqual({
        id: 'u1',
        name: 'Alice',
        email: 'alice@example.com',
        role: 'Contributor',
        mustChangePassword: false,
      });
      expect(second).toBe(first);
      expect(mockAuthMe).toHaveBeenCalledTimes(1);
    });
  });

  describe('getUserById', () => {
    it('returns cached user without refetching', async () => {
      mockAuthMe.mockResolvedValue({
        userId: 'u1',
        name: 'Alice',
        email: 'alice@example.com',
        role: 'Contributor',
        mustChangePassword: false,
      });
      await userService.getCurrentUser();

      const cached = await userService.getUserById('u1');
      expect(cached?.name).toBe('Alice');
      expect(mockGetUserById).not.toHaveBeenCalled();
    });

    it('fetches and caches user by id', async () => {
      mockGetUserById.mockResolvedValue({
        id: 'u2',
        name: 'Bob',
        email: 'bob@example.com',
        role: 'Viewer',
        mustChangePassword: true,
      });

      const user = await userService.getUserById('u2');
      expect(user).toEqual({
        id: 'u2',
        name: 'Bob',
        email: 'bob@example.com',
        role: 'Viewer',
        mustChangePassword: true,
      });

      await userService.getUserById('u2');
      expect(mockGetUserById).toHaveBeenCalledTimes(1);
    });

    it('returns null when fetch fails', async () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
      mockGetUserById.mockRejectedValue(new Error('not found'));

      const user = await userService.getUserById('missing');
      expect(user).toBeNull();
      warnSpy.mockRestore();
    });
  });

  describe('getUserForMessage', () => {
    it('returns current user when userId is omitted', async () => {
      mockAuthMe.mockResolvedValue({
        userId: 'u1',
        name: 'Alice',
        email: 'alice@example.com',
        role: 'Contributor',
        mustChangePassword: false,
      });

      const user = await userService.getUserForMessage();
      expect(user?.id).toBe('u1');
    });

    it('returns current user when userId matches current user', async () => {
      mockAuthMe.mockResolvedValue({
        userId: 'u1',
        name: 'Alice',
        email: 'alice@example.com',
        role: 'Contributor',
        mustChangePassword: false,
      });

      const user = await userService.getUserForMessage('u1');
      expect(user?.name).toBe('Alice');
      expect(mockGetUserById).not.toHaveBeenCalled();
    });

    it('fetches other users by id', async () => {
      mockAuthMe.mockResolvedValue({
        userId: 'u1',
        name: 'Alice',
        email: 'alice@example.com',
        role: 'Contributor',
        mustChangePassword: false,
      });
      mockGetUserById.mockResolvedValue({
        id: 'u2',
        name: 'Bob',
        email: 'bob@example.com',
        role: 'Viewer',
        mustChangePassword: false,
      });

      const user = await userService.getUserForMessage('u2');
      expect(user?.name).toBe('Bob');
      expect(mockGetUserById).toHaveBeenCalledWith('u2');
    });
  });

  describe('getUserInitials', () => {
    it.each([
      [null, 'U'],
      [{ name: 'Alice' }, 'A'],
      [{ name: 'Alice Smith' }, 'AS'],
      [{ email: 'alice@example.com' }, 'A'],
      [{ email: 'alice.smith@example.com' }, 'AS'],
      [{ email: 'bob_jones@example.com' }, 'BJ'],
      [{}, 'U'],
    ])('returns initials for %j', (userInfo, expected) => {
      expect(userService.getUserInitials(userInfo)).toBe(expected);
    });
  });

  describe('clearCache', () => {
    it('forces refetch of current user', async () => {
      mockAuthMe.mockResolvedValue({
        userId: 'u1',
        name: 'Alice',
        email: 'alice@example.com',
        role: 'Contributor',
        mustChangePassword: false,
      });

      await userService.getCurrentUser();
      userService.clearCache();
      await userService.getCurrentUser();

      expect(mockAuthMe).toHaveBeenCalledTimes(2);
    });
  });
});
