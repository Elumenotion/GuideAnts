import { Link } from 'react-router-dom';

const Terms: React.FC = () => {
    return (
        <div className="h-screen overflow-y-auto bg-gray-50 text-gray-900">
            <div className="max-w-3xl mx-auto px-6 py-10">
                <header className="mb-6">
                    <Link to="/login" className="inline-flex items-center text-sm text-blue-700 hover:underline">
                        ← Back to Sign in
                    </Link>
                </header>

                <div className="bg-white rounded-xl shadow-sm border border-gray-200">
                    <div className="px-6 py-6 border-b border-gray-100 flex items-center gap-3">
                        <img src="/guide.png" alt="GuideAnts logo" className="w-8 h-8" />
                        <div>
                            <h1 className="text-2xl font-bold">Terms of Service</h1>
                            <p className="text-sm text-gray-600">Effective date: January 1, 2025</p>
                        </div>
                    </div>

                    <div className="px-6 py-6 space-y-8">
                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">1. Acceptance of Terms</h2>
                            <p className="text-gray-700 leading-7">
                                By accessing or using GuideAnts Notebooks (the "Service"), you agree to be bound by these
                                Terms of Service (the "Terms"). If you do not agree to all Terms, you may not use the Service.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">2. Description of Service</h2>
                            <p className="text-gray-700 leading-7">
                                The Service provides AI-assisted notebook and project collaboration tools. Features may
                                evolve over time and may vary based on your plan.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">3. Accounts and Access</h2>
                            <ul className="list-disc pl-6 text-gray-700 leading-7 space-y-1">
                                <li>You must use a valid Microsoft work, school, or personal account.</li>
                                <li>You are responsible for maintaining the confidentiality of your account and credentials.</li>
                                <li>You are responsible for all activities that occur under your account.</li>
                            </ul>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">4. Acceptable Use</h2>
                            <ul className="list-disc pl-6 text-gray-700 leading-7 space-y-1">
                                <li>Do not use the Service to violate any law or the rights of others.</li>
                                <li>Do not attempt to disrupt or compromise the Service or underlying infrastructure.</li>
                                <li>Do not upload malicious code or content.</li>
                            </ul>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">5. Content</h2>
                            <p className="text-gray-700 leading-7">
                                You retain ownership of content you submit. You grant us a limited license to process your
                                content solely to provide and improve the Service. You are responsible for ensuring you have the
                                necessary rights to the content you upload.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">6. Privacy</h2>
                            <p className="text-gray-700 leading-7">
                                Your use of the Service is subject to our <Link to="/privacy" className="text-blue-700 hover:underline">Privacy Policy</Link>.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">7. Plans and Billing</h2>
                            <p className="text-gray-700 leading-7">
                                Certain features require a paid plan. Fees are billed in accordance with the plan you select and
                                are non-refundable except where required by law.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">8. Termination</h2>
                            <p className="text-gray-700 leading-7">
                                We may suspend or terminate access if you violate these Terms or use the Service in a manner
                                that poses risk to the platform or other users. You may stop using the Service at any time.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">9. Disclaimers and Limitation of Liability</h2>
                            <p className="text-gray-700 leading-7">
                                The Service is provided "as is" without warranties of any kind. To the maximum extent permitted by
                                law, we disclaim all warranties and will not be liable for indirect, incidental, or consequential
                                damages arising from your use of the Service.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">10. Changes to Terms</h2>
                            <p className="text-gray-700 leading-7">
                                We may modify these Terms from time to time. Material changes will be communicated, and your
                                continued use of the Service constitutes acceptance of the updated Terms.
                            </p>
                        </section>

                        <section className="space-y-3">
                            <h2 className="text-lg font-semibold text-gray-900">11. Contact</h2>
                            <p className="text-gray-700 leading-7">
                                Questions about these Terms can be directed to support@guideants.ai.
                            </p>
                        </section>
                    </div>

                    <div className="px-6 py-6 border-t border-gray-100 text-sm text-gray-600">
                        © {new Date().getFullYear()} Elumenotion, LLC. All rights reserved.
                    </div>
                </div>

                <footer className="mt-6 text-xs text-gray-500 text-center">
                    © {new Date().getFullYear()} Elumenotion, LLC. All rights reserved.
                </footer>
            </div>
        </div>
    );
};

export default Terms;


