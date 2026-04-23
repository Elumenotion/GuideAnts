import { useCallback, useEffect, useRef, useState } from 'react';

function OnboardingCarousel() {
  const [currentIndex, setCurrentIndex] = useState(0);
  const slideCount = 3;
  const trackRef = useRef<HTMLDivElement | null>(null);

  const goTo = useCallback((index: number) => {
    const next = (index + slideCount) % slideCount;
    setCurrentIndex(next);
  }, [slideCount]);

  const goPrev = useCallback(() => goTo(currentIndex - 1), [currentIndex, goTo]);
  const goNext = useCallback(() => goTo(currentIndex + 1), [currentIndex, goTo]);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'ArrowLeft') {
        e.preventDefault();
        goPrev();
      } else if (e.key === 'ArrowRight') {
        e.preventDefault();
        goNext();
      }
    };
    const node = trackRef.current;
    node?.addEventListener('keydown', handler);
    return () => node?.removeEventListener('keydown', handler);
  }, [goNext, goPrev]);

  // Advance slide on each pulse of the Home page Quick Start button's animation
  useEffect(() => {
    const quickStartButton = document.querySelector('[data-tour-id="home.quick-start"]');
    if (!quickStartButton) return;

    const onPulse = (_e: AnimationEvent) => {
      goNext();
    };

    quickStartButton.addEventListener('animationiteration', onPulse as unknown as EventListener);
    return () => quickStartButton.removeEventListener('animationiteration', onPulse as unknown as EventListener);
  }, [goNext]);

  const focusQuickStart = useCallback(() => {
    const btn = document.querySelector('[data-tour-id="home.quick-start"]') as HTMLButtonElement | null;
    if (btn) {
      btn.scrollIntoView({ behavior: 'smooth', block: 'center' });
      try { btn.focus({ preventScroll: true } as any); } catch { btn.focus(); }
      btn.classList.add('ring-2', 'ring-offset-2', 'ring-blue-500');
      window.setTimeout(() => btn.classList.remove('ring-2', 'ring-offset-2', 'ring-blue-500'), 2000);
    }
  }, []);

  return (
    <div className="flex flex-col items-center justify-center py-16 px-4">
      <div
        className="relative w-full max-w-3xl"
        role="region"
        aria-roledescription="carousel"
        aria-label="Getting started"
      >
        <div className="overflow-hidden rounded-2xl shadow-2xl ring-1 ring-black/5">
          <div
            ref={trackRef}
            className="flex focus:outline-none"
            tabIndex={0}
            style={{ transform: `translateX(-${currentIndex * 100}%)`, transition: 'transform 500ms ease' }}
          >
            {/* Slide 1 - Welcome */}
            <section className="min-w-full py-8 px-14 md:py-10 md:px-16 bg-gradient-to-br from-red-600 via-rose-600 to-zinc-900 text-white">
              <div className="flex flex-col md:flex-row items-center gap-8">
                <div className="flex-1">
                  <h2 className="text-2xl md:text-3xl font-semibold tracking-tight mb-3">Collaborate Intelligently. Get Work Done Consistently.</h2>
                  <p className="text-white/90 text-sm md:text-base max-w-prose">
                    GuideAnts Notebooks brings collaborators together with AI-powered workspaces: organize projects, share
                    files, and solve problems - fast and securely.
                  </p>
                </div>
                <div className="flex-1 flex items-center justify-center">
                  <img
                    src="/guide.png"
                    alt="GuideAnts logo"
                    className="w-40 h-40 md:w-48 md:h-48 rounded-full bg-white p-2 shadow-2xl ring-4 ring-white/20 object-contain"
                    loading="lazy"
                    decoding="async"
                  />
                </div>
              </div>
              <p className="sr-only">Slide 1 of 3</p>
            </section>

            {/* Slide 2 - Tour button hint */}
            <section className="min-w-full py-8 px-14 md:py-10 md:px-16 bg-white">
              <div className="flex flex-col md:flex-row items-center gap-8">
                <div className="flex-1">
                  <h2 className="text-2xl md:text-3xl font-semibold tracking-tight mb-3 text-gray-900">Need help? Start a tour</h2>
                  <p className="text-gray-600 text-sm md:text-base max-w-prose mb-4">
                    Look for this help icon in the upper right corner of every page to launch a guided tour and learn the features right where
                    you are.
                  </p>
                  <p className="text-gray-500 text-xs md:text-sm">You can re-run tours any time from the help icon.</p>
                </div>
                <div className="flex-1 flex items-center justify-center">
                  <div className="w-20 h-20 md:w-24 md:h-24 rounded-full bg-white text-gray-900 flex items-center justify-center shadow-lg ring-4 ring-gray-900/10">
                    <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor" className="w-8 h-8">
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9.879 7.519c1.171-1.025 3.071-1.025 4.242 0 1.172 1.025 1.172 2.687 0 3.712-.203.179-.43.326-.67.442-.745.361-1.45.999-1.45 1.827v.75M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Zm-9 5.25h.008v.008H12v-.008Z" />
                    </svg>
                    <span className="sr-only">Help</span>
                  </div>
                </div>
              </div>
              <p className="sr-only">Slide 2 of 3</p>
            </section>

            {/* Slide 3 - Quick Start CTA */}
            <section className="min-w-full py-8 px-14 md:py-10 md:px-16 bg-gradient-to-br from-blue-600 to-indigo-600 text-white">
              <div className="flex flex-col md:flex-row items-center gap-8">
                <div className="flex-1">
                  <h2 className="text-2xl md:text-3xl font-semibold tracking-tight mb-3">Jump right in with Quick Start</h2>
                  <p className="text-white/90 text-sm md:text-base max-w-prose mb-6">
                    Click the <strong>Quick Start</strong> button above to instantly create a project, notebook, and conversation.
                  </p>
                  <div className="flex items-center gap-3">
                    <button
                      onClick={focusQuickStart}
                      className="px-4 py-2 text-sm font-medium rounded-md bg-white text-blue-700 hover:bg-blue-50 transition-colors"
                    >
                      Scroll to Quick Start
                    </button>
                  </div>
                </div>
              </div>
              <p className="sr-only">Slide 3 of 3</p>
            </section>
          </div>
        </div>

        {/* Controls */}
        <button
          type="button"
          onClick={goPrev}
          className="absolute left-3 top-1/2 -translate-y-1/2 rounded-full bg-white/90 hover:bg-white text-gray-700 shadow-md w-9 h-9 flex items-center justify-center ring-1 ring-black/5"
          aria-label="Previous slide"
        >
          <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" className="w-5 h-5">
            <path fillRule="evenodd" d="M15.53 4.47a.75.75 0 0 1 0 1.06L9.06 12l6.47 6.47a.75.75 0 1 1-1.06 1.06l-7-7a.75.75 0 0 1 0-1.06l7-7a.75.75 0 0 1 1.06 0Z" clipRule="evenodd" />
          </svg>
        </button>
        <button
          type="button"
          onClick={goNext}
          className="absolute right-3 top-1/2 -translate-y-1/2 rounded-full bg-white/90 hover:bg-white text-gray-700 shadow-md w-9 h-9 flex items-center justify-center ring-1 ring-black/5"
          aria-label="Next slide"
        >
          <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" className="w-5 h-5">
            <path fillRule="evenodd" d="M8.47 19.53a.75.75 0 0 1 0-1.06L14.94 12 8.47 5.53a.75.75 0 0 1 1.06-1.06l7 7a.75.75 0 0 1 0 1.06l-7 7a.75.75 0 0 1-1.06 0Z" clipRule="evenodd" />
          </svg>
        </button>

        {/* Dots */}
        <div className="mt-4 flex items-center justify-center gap-2" role="tablist" aria-label="Slide selector">
          {Array.from({ length: slideCount }).map((_, i) => (
            <button
              key={i}
              type="button"
              role="tab"
              aria-selected={i === currentIndex}
              aria-label={`Go to slide ${i + 1}`}
              className={`h-2.5 rounded-full transition-all ${i === currentIndex ? 'w-6 bg-gray-800' : 'w-2.5 bg-gray-300'}`}
              onClick={() => goTo(i)}
            />
          ))}
        </div>
      </div>
    </div>
  );
}

const EmptyStateVideo = () => {
  return <OnboardingCarousel />;
};

export default EmptyStateVideo;

