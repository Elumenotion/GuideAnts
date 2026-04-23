export type Role = 'Owner' | 'Contributor' | 'Reader';

export interface PermissionContext {
    userEmail: string;
    roles: Array<{ userEmail: string; roleName: string }>;
}

export interface Permissions {
    isOwner: boolean;
    canEdit: boolean;
    effectiveRole: Role | undefined;
    isEffectivelyReadOnly: boolean;
    reason?: string;
}

export function getPermissions(_ctx: PermissionContext): Permissions {
    return {
        isOwner: true,
        canEdit: true,
        effectiveRole: 'Owner',
        isEffectivelyReadOnly: false,
    };
}
