// The project site (issue #545): served by GitHub Pages at nid.mistribe.com (public/CNAME), French at the root, English under /en/.
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  site: 'https://nid.mistribe.com',
  vite: { plugins: [tailwindcss()] },
  integrations: [
    starlight({
      title: 'Nid',
      favicon: '/favicon.svg',
      defaultLocale: 'root',
      locales: {
        root: { label: 'Français', lang: 'fr' },
        en: { label: 'English', lang: 'en' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/Mistribe/Nid' }],
      customCss: ['./src/styles/global.css'],
      plugins: [starlightLinksValidator()],
    }),
  ],
});
