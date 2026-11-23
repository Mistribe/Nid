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
      // The app's wordmark (web/public/wordmark.svg), filled for each theme.
      logo: { light: './src/assets/wordmark-light.svg', dark: './src/assets/wordmark-dark.svg', alt: 'Nid', replacesTitle: true },
      defaultLocale: 'root',
      locales: {
        root: { label: 'Français', lang: 'fr' },
        en: { label: 'English', lang: 'en' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/Mistribe/Nid' }],
      customCss: ['./src/styles/global.css'],
      components: { Footer: './src/components/Footer.astro' },
      sidebar: [
        { label: 'Fonctionnalités', translations: { en: 'Features' }, items: [{ autogenerate: { directory: 'fonctionnalites' } }] },
        { label: 'Installer', translations: { en: 'Install' }, items: [{ autogenerate: { directory: 'installer' } }] },
        { label: 'Sous le capot', translations: { en: 'Under the hood' }, link: '/sous-le-capot/' },
      ],
      plugins: [starlightLinksValidator()],
    }),
  ],
});
