/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Set by the image build (web/Dockerfile, VERSION); absent in development. */
  readonly VITE_NID_VERSION?: string;
}
