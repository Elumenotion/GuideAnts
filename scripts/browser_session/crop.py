"""Monitor-relative crop math for foreground windows."""

from __future__ import annotations

from scripts.browser_session.schema import CropRect, MonitorGeometry, ScreenRect


def screen_to_crop(
    screen: ScreenRect,
    monitor: MonitorGeometry,
) -> tuple[CropRect, bool, bool]:
    """Convert a screen-space window rect to monitor-local video crop pixels.

    Returns ``(crop, visible_on_monitor, clamped)``.
    """
    mon_right = monitor.left + monitor.width
    mon_bottom = monitor.top + monitor.height
    win_right = screen.left + screen.width
    win_bottom = screen.top + screen.height

    overlap_left = max(screen.left, monitor.left)
    overlap_top = max(screen.top, monitor.top)
    overlap_right = min(win_right, mon_right)
    overlap_bottom = min(win_bottom, mon_bottom)

    if overlap_right <= overlap_left or overlap_bottom <= overlap_top:
        return (
            CropRect(x=0, y=0, w=0, h=0),
            False,
            False,
        )

    crop_x = overlap_left - monitor.left
    crop_y = overlap_top - monitor.top
    crop_w = overlap_right - overlap_left
    crop_h = overlap_bottom - overlap_top

    clamped = (
        screen.left < monitor.left
        or screen.top < monitor.top
        or win_right > mon_right
        or win_bottom > mon_bottom
    )

    return (
        CropRect(x=crop_x, y=crop_y, w=crop_w, h=crop_h),
        True,
        clamped,
    )


def rects_equal(a: ScreenRect, b: ScreenRect, threshold: int = 2) -> bool:
    return (
        abs(a.left - b.left) <= threshold
        and abs(a.top - b.top) <= threshold
        and abs(a.width - b.width) <= threshold
        and abs(a.height - b.height) <= threshold
    )
