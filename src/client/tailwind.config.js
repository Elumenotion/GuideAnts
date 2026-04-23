/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  theme: {
    extend: {
      backgroundImage: {
        'checkered': 'repeating-conic-gradient(#808080 0% 25%, #404040 0% 50%) 50% / 20px 20px',
      },
    },
  },
  plugins: [],
} 