import { useEffect } from 'react';
import { useTour } from './TourProvider';

type JoyrideStep = {
  target: string;
  content: string;
  placement?: 'top' | 'bottom' | 'left' | 'right' | 'auto';
  disableBeacon?: boolean;
  spotlightPadding?: number;
  popoverOffset?: number; // Distance from element (perpendicular)
  popoverAlignOffset?: number; // Position along edge (parallel)
  onHighlight?: () => void;
  onDeselect?: () => void;
};

export function useRegisterTour(screenId: string, steps: JoyrideStep[]) {
  const { registerTourSteps } = useTour();
  useEffect(() => registerTourSteps(screenId, steps), [registerTourSteps, screenId, steps]);
}


