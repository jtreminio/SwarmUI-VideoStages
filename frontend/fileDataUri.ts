export const fileAsDataUri = (file: File): Promise<string | null> =>
    new Promise((resolve) => {
        const reader = new FileReader();
        reader.onerror = () => resolve(null);
        reader.onload = () => {
            const data = `${reader.result ?? ""}`;
            resolve(data || null);
        };
        reader.readAsDataURL(file);
    });
