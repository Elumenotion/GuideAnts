import {
  Children,
  isValidElement,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { FiMoreHorizontal } from 'react-icons/fi';

type Variant = 'header' | 'toolbar';

const MORE_BUTTON_WIDTH: Record<Variant, number> = {
  header: 40, // h-10 w-10 icon button
  toolbar: 34, // compact toolbar button
};

interface HeaderActionsBarProps {
  children: ReactNode;
  /**
   * Extra classes appended to the bar. The bar always carries `flex-1 min-w-0`
   * so that in a flex row it receives a bounded, layout-driven width
   * (independent of its own contents) and can decide how many buttons fit
   * before collapsing the rest. In a grid cell pass e.g. `w-full justify-self-end`.
   */
  className?: string;
  /** Where the buttons (and the collapsed "⋯" control) sit within the bar. */
  align?: 'start' | 'end';
  /** Visual style of the collapsed "⋯" button. */
  variant?: Variant;
}

function isDivider(child: ReactNode): boolean {
  return (
    isValidElement(child) &&
    typeof (child.props as { className?: unknown }).className === 'string' &&
    ((child.props as { className: string }).className.includes('toolbar-divider'))
  );
}

/**
 * Renders a single row of action buttons that never wraps. When the available
 * width is not enough for every button, the trailing buttons collapse into a
 * "⋯" menu that keeps the same relative position the buttons occupied
 * (rightmost for `align="end"`, end-of-group for `align="start"`). Width is
 * measured, so collapsing adapts to the actual buttons present rather than to
 * fixed breakpoints.
 *
 * Children whose className contains `toolbar-divider` are treated as separators:
 * they never start the overflow menu and never end the visible run.
 */
export function HeaderActionsBar({
  children,
  className = 'flex-1 min-w-0',
  align = 'end',
  variant = 'header',
}: HeaderActionsBarProps) {
  const items = useMemo(() => Children.toArray(children).filter(Boolean), [children]);
  const total = items.length;

  const containerRef = useRef<HTMLDivElement>(null);
  const itemRefs = useRef<Array<HTMLSpanElement | null>>([]);
  const widthsRef = useRef<number[]>([]);
  const measuredRef = useRef(false);
  const menuRef = useRef<HTMLDivElement>(null);

  const [visibleCount, setVisibleCount] = useState(total);
  const [menuOpen, setMenuOpen] = useState(false);

  const recompute = useCallback(() => {
    const container = containerRef.current;
    if (!container || !measuredRef.current) {
      return;
    }
    const widths = widthsRef.current;
    const n = widths.length;
    if (n === 0) {
      return;
    }
    const gap = parseFloat(getComputedStyle(container).columnGap) || 0;
    const available = container.clientWidth;
    const moreWidth = MORE_BUTTON_WIDTH[variant];

    const sumAll = widths.reduce((acc, w) => acc + w, 0) + gap * Math.max(0, n - 1);
    if (sumAll <= available + 0.5) {
      setVisibleCount(n);
      return;
    }

    let fit = 0;
    let prefix = 0;
    for (let i = 0; i < n; i += 1) {
      prefix += widths[i];
      // i + 1 visible buttons plus the trailing more button => (i + 1) gaps.
      const required = prefix + moreWidth + gap * (i + 1);
      if (required <= available + 0.5) {
        fit = i + 1;
      } else {
        break;
      }
    }
    setVisibleCount(Math.min(fit, n - 1));
  }, [variant]);

  // Reset measurement whenever the set of children changes; render them all so
  // their widths can be captured before deciding what fits.
  useLayoutEffect(() => {
    measuredRef.current = false;
    widthsRef.current = [];
    setVisibleCount(total);
    setMenuOpen(false);
  }, [total]);

  useLayoutEffect(() => {
    if (measuredRef.current || visibleCount !== total) {
      return;
    }
    const measured = itemRefs.current
      .slice(0, total)
      .map((el) => (el ? el.getBoundingClientRect().width : 0));
    // In environments without layout (e.g. jsdom) widths are 0; skip collapsing
    // entirely so every child stays rendered and queryable.
    if (measured.length === total && total > 0 && measured.every((w) => w > 0)) {
      widthsRef.current = measured;
      measuredRef.current = true;
      recompute();
    }
  });

  useEffect(() => {
    const container = containerRef.current;
    if (!container) {
      return;
    }
    const observer = new ResizeObserver(() => recompute());
    observer.observe(container);
    return () => observer.disconnect();
  }, [recompute]);

  useEffect(() => {
    if (!menuOpen) {
      return;
    }
    const onPointerDown = (event: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setMenuOpen(false);
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [menuOpen]);

  // Don't end the visible run on a divider.
  let splitAt = measuredRef.current && visibleCount < total ? visibleCount : total;
  while (splitAt > 0 && splitAt < total && isDivider(items[splitAt - 1])) {
    splitAt -= 1;
  }

  const hasOverflow = splitAt < total;
  const inlineItems = hasOverflow ? items.slice(0, splitAt) : items;
  // Dividers are meaningless in the vertical overflow menu.
  const overflowItems = hasOverflow ? items.slice(splitAt).filter((child) => !isDivider(child)) : [];

  const moreButtonClass =
    variant === 'toolbar'
      ? `flex h-7 shrink-0 items-center justify-center rounded px-2 text-gray-600 transition-colors ${
          menuOpen ? 'bg-gray-200' : 'hover:bg-gray-200'
        }`
      : `h-10 w-10 border rounded-md transition-colors flex items-center justify-center text-gray-700 bg-white ${
          menuOpen ? 'border-blue-400 bg-blue-50' : 'border-gray-300 hover:bg-gray-50'
        }`;

  return (
    <div
      ref={containerRef}
      className={`flex min-w-0 items-center gap-1 sm:gap-2 ${
        align === 'start' ? 'justify-start' : 'justify-end'
      } ${className}`.trim()}
    >
      {inlineItems.map((child, index) => (
        <span
          key={index}
          ref={(el) => {
            itemRefs.current[index] = el;
          }}
          className="flex shrink-0 items-center"
        >
          {child}
        </span>
      ))}

      {hasOverflow ? (
        <div ref={menuRef} className="relative flex shrink-0 items-center">
          <button
            type="button"
            aria-label="More actions"
            aria-haspopup="menu"
            aria-expanded={menuOpen}
            title="More"
            onClick={() => setMenuOpen((open) => !open)}
            className={moreButtonClass}
          >
            <FiMoreHorizontal className="h-4 w-4" />
            <span className="sr-only">More actions</span>
          </button>

          {menuOpen ? (
            <div
              role="menu"
              className="absolute right-0 top-full z-50 mt-2 flex flex-col items-stretch gap-1 rounded-md border border-gray-200 bg-white p-1.5 shadow-lg"
            >
              {overflowItems.map((child, index) => (
                <div
                  key={index}
                  className={`flex items-center ${align === 'start' ? 'justify-start' : 'justify-end'}`}
                >
                  {child}
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
