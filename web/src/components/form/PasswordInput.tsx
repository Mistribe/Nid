import { Eye, EyeOff } from 'lucide-react';
import { useState, type ComponentProps } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

/**
 * A password field a password manager understands (local-accounts.md §4): pasting allowed, no length cap below the
 * server's 256, a button to show what was typed. `autoComplete` says which: `current-password` or `new-password`.
 * shadcn/ui has no password input: this one is an Input and a Button.
 */
export function PasswordInput({ autoComplete, ...props }: ComponentProps<typeof Input> & { autoComplete: 'current-password' | 'new-password' }) {
  const { t } = useTranslation();
  const [shown, setShown] = useState(false);
  return (
    <div className="relative">
      <Input {...props} type={shown ? 'text' : 'password'} autoComplete={autoComplete} spellCheck={false} autoCapitalize="none" className="pr-10" />
      <Button type="button" variant="ghost" size="icon" className="absolute top-0 right-0 h-9 w-9"
        aria-label={t(shown ? 'local_auth.hide_password' : 'local_auth.show_password')} aria-pressed={shown}
        onClick={() => setShown((value) => !value)}>
        {shown ? <EyeOff aria-hidden /> : <Eye aria-hidden />}
      </Button>
    </div>
  );
}
