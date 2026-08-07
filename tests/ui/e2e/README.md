# Browser UI tests

Place Playwright `*.spec.js` files in this directory. The shared configuration starts
the Workbench on `http://127.0.0.1:5042` and runs tests in Chromium.

No feature-specific browser scenarios are defined yet. `npm run test:ui` is configured
to succeed with an empty suite so the infrastructure can land independently.
